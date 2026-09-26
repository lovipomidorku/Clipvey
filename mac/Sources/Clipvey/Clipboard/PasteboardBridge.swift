import AppKit
import ImageIO
import UniformTypeIdentifiers

/// Мост между буфером обмена macOS и узлом Clipvey.
/// macOS не сообщает об изменениях буфера, поэтому changeCount проверяется дважды в секунду.
/// Само содержимое читается, только когда есть подключённые устройства.
///
/// Текст или картинка. Если в буфере и то и другое, отправляется одно:
/// - картинка — если текста нет или это только ссылка (так копирует картинку браузер: данные картинки + URL);
/// - текст — в остальных случаях: офисные программы кладут рядом с текстом его изображение
///   (ячейки Excel, фрагмент Word), и пользователь копировал именно текст;
/// - скопированный в Finder файл (public.file-url) картинкой не считается: его значок не отправляется, как и раньше.
@MainActor
final class PasteboardBridge {
    /// Отметка «пришло с другого устройства»: такое содержимое не отправляется обратно.
    static let remoteMarker = NSPasteboard.PasteboardType("io.github.lovipomidorku.clipvey.remote")
    /// Так менеджеры паролей помечают секретное содержимое (nspasteboard.org).
    private static let secretTypes: Set<String> = ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType"]
    private static let maxBytes = ProtocolLimits.maxClipBytes
    private static let jpegType = NSPasteboard.PasteboardType(UTType.jpeg.identifier)
    private static let heicType = NSPasteboard.PasteboardType(UTType.heic.identifier)
    /// Типы картинок в порядке предпочтения: PNG и JPEG как есть, HEIC → JPEG, TIFF → PNG.
    private static let imageTypes: [NSPasteboard.PasteboardType] = [.png, jpegType, heicType, .tiff]
    /// Больше этого исходные данные даже не конвертируются (TIFF без сжатия бывает огромным).
    private static let maxSourceImageBytes = 256 * 1024 * 1024

    private let pasteboard: NSPasteboard
    private var lastChangeCount: Int
    private var timer: Timer?

    var shouldRead: () -> Bool = { false }
    /// Отправлять ли картинки (включено «Передавать картинки» и есть кому).
    var shouldReadImages: () -> Bool = { false }
    var onCopy: (String) -> Void = { _ in }
    var onCopyImage: (_ data: Data, _ mime: String) -> Void = { _, _ in }

    init(pasteboard: NSPasteboard) {
        self.pasteboard = pasteboard
        lastChangeCount = pasteboard.changeCount
    }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
    }

    /// Нужно ли разрешение пользователя на чтение буфера (macOS 15.4+ спрашивает при программном чтении).
    var needsAccessPermission: Bool {
        guard pasteboard.name == .general else { return false }
        if #available(macOS 15.4, *) {
            return pasteboard.accessBehavior != .alwaysAllow
        }
        return false
    }

    /// Записать текст, пришедший с другого устройства. Без «только этот Mac», чтобы он дошёл до iPhone.
    func write(_ text: String) {
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        pasteboard.setString("1", forType: Self.remoteMarker)
        lastChangeCount = pasteboard.changeCount
        Log.clipboard.info("Записано в буфер: \(text.count) символов")
    }

    /// Записать картинку, пришедшую с другого устройства: PNG как public.png, JPEG как public.jpeg, вместе с маркером.
    func writeImage(_ data: Data, mime: String) {
        pasteboard.clearContents()
        pasteboard.setData(data, forType: mime == "image/jpeg" ? Self.jpegType : .png)
        pasteboard.setString("1", forType: Self.remoteMarker)
        lastChangeCount = pasteboard.changeCount
        Log.clipboard.info("Записана в буфер картинка \(mime, privacy: .public): \(data.count) байт")
    }

    private func poll() {
        let count = pasteboard.changeCount
        guard count != lastChangeCount else { return }
        lastChangeCount = count
        guard shouldRead() else { return }

        let types = pasteboard.types ?? []
        if types.contains(Self.remoteMarker) {
            return
        }
        if types.contains(where: { Self.secretTypes.contains($0.rawValue) }) {
            Log.clipboard.info("Секретное содержимое (пароль) не передаётся")
            return
        }
        let text = pasteboard.string(forType: .string)
        if shouldReadImages(), !types.contains(.fileURL), Self.textIsAuxiliary(text),
           let imageType = Self.imageTypes.first(where: types.contains),
           let data = pasteboard.data(forType: imageType), !data.isEmpty {
            sendImage(data, type: imageType)
            return
        }
        guard let text, !text.isEmpty else { return }
        guard text.utf8.count <= Self.maxBytes else {
            Log.clipboard.notice("Текст больше 1 МиБ — не передаётся")
            return
        }
        onCopy(text)
    }

    /// Текст рядом с картинкой ничего не значит: его нет или это одна ссылка без пробелов.
    private static func textIsAuxiliary(_ text: String?) -> Bool {
        guard let trimmed = text?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return true }
        return !trimmed.contains(where: \.isWhitespace) && (trimmed.contains("://") || trimmed.hasPrefix("data:"))
    }

    /// Конвертация — не на главном потоке: большой TIFF переводится в PNG заметное время.
    private func sendImage(_ data: Data, type: NSPasteboard.PasteboardType) {
        guard data.count <= Self.maxSourceImageBytes else {
            Log.clipboard.notice("Картинка в буфере \(data.count) байт — слишком большая, не передаётся")
            return
        }
        Task.detached(priority: .userInitiated) { [weak self] in
            let converted = Self.convert(data, type: type)
            await MainActor.run {
                guard let self else { return }
                guard let (image, mime) = converted else {
                    Log.clipboard.notice("Не удалось прочитать картинку \(type.rawValue, privacy: .public) из буфера")
                    return
                }
                guard image.count <= ProtocolLimits.maxImageBytes else {
                    Log.clipboard.notice("Картинка \(image.count) байт больше 20 МиБ — не передаётся")
                    return
                }
                self.onCopyImage(image, mime)
            }
        }
    }

    /// PNG и JPEG — как есть; HEIC → JPEG, TIFF → PNG через ImageIO.
    nonisolated private static func convert(_ data: Data, type: NSPasteboard.PasteboardType) -> (Data, String)? {
        switch type {
        case .png:
            return (data, "image/png")
        case jpegType:
            return (data, "image/jpeg")
        case heicType:
            return recode(data, as: .jpeg, options: [kCGImageDestinationLossyCompressionQuality: 0.9]).map { ($0, "image/jpeg") }
        default:
            return recode(data, as: .png, options: [:]).map { ($0, "image/png") }
        }
    }

    nonisolated private static func recode(_ data: Data, as type: UTType, options: [CFString: Any]) -> Data? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil), CGImageSourceGetCount(source) > 0 else { return nil }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output as CFMutableData, type.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImageFromSource(destination, source, 0, options as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }
}
