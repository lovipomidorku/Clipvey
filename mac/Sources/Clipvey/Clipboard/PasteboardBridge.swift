import AppKit
import ImageIO
import UniformTypeIdentifiers

/// Мост между буфером обмена macOS и узлом Clipvey.
/// macOS не сообщает об изменениях буфера, поэтому changeCount проверяется дважды в секунду.
/// Само содержимое читается, только когда есть подключённые устройства.
///
/// Файлы, картинка или текст — отправляется одно:
/// - файлы и папки (public.file-url, как их кладёт Finder) — важнее всего: имя файла текстом и значок картинкой,
///   которые Finder кладёт рядом, не отправляются. Если файлы принять некому (выключено «Передавать файлы» или
///   у подключённых устройств нет file в caps), не отправляется ничего: имя файла другому устройству ни к чему;
/// - картинка — если текста нет или это только ссылка (так копирует картинку браузер: данные картинки + URL);
/// - текст — в остальных случаях: офисные программы кладут рядом с текстом его изображение
///   (ячейки Excel, фрагмент Word), и пользователь копировал именно текст.
@MainActor
final class PasteboardBridge {
    /// Отметка «пришло с другого устройства»: такое содержимое не отправляется обратно.
    static let remoteMarker = NSPasteboard.PasteboardType("io.github.lovipomidorku.clipvey.remote")
    /// Так менеджеры паролей помечают секретное содержимое (nspasteboard.org).
    private static let secretTypes: Set<String> = ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType"]
    private static let maxBytes = ProtocolLimits.maxClipBytes
    private static let jpegType = NSPasteboard.PasteboardType(UTType.jpeg.identifier)
    private static let heicType = NSPasteboard.PasteboardType(UTType.heic.identifier)
    /// Типы картинок: PNG и JPEG отправляются как есть, HEIC → JPEG, TIFF → PNG.
    private static let imageTypes: [NSPasteboard.PasteboardType] = [.png, jpegType, heicType, .tiff]
    /// Больше этого исходные данные даже не конвертируются (TIFF без сжатия бывает огромным).
    private static let maxSourceImageBytes = 256 * 1024 * 1024

    private let pasteboard: NSPasteboard
    private var lastChangeCount: Int
    private var timer: Timer?

    var shouldRead: () -> Bool = { false }
    /// Отправлять ли картинки (включено «Передавать картинки» и есть кому).
    var shouldReadImages: () -> Bool = { false }
    /// Отправлять ли файлы (включено «Передавать файлы» и есть кому).
    var shouldReadFiles: () -> Bool = { false }
    var onCopy: (String) -> Void = { _ in }
    var onCopyImage: (_ data: Data, _ mime: String) -> Void = { _, _ in }
    /// Скопированы файлы и папки (пути, а не ссылки на файлы Finder).
    var onCopyFiles: ([URL]) -> Void = { _ in }
    /// Буфер изменил кто-то другой (не запись этого моста) — даже если читать его сейчас не нужно.
    var onExternalChange: () -> Void = {}

    init(pasteboard: NSPasteboard) {
        self.pasteboard = pasteboard
        lastChangeCount = pasteboard.changeCount
    }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
    }

    /// Счётчик изменений буфера: по нему видно, что содержимое сменилось (своя запись его тоже меняет).
    var changeCount: Int { pasteboard.changeCount }

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

    /// Записать файлы и папки, пришедшие с другого устройства: public.file-url каждого, вместе с маркером.
    /// Finder вставляет их как обычные скопированные файлы.
    func writeFiles(_ urls: [URL]) {
        pasteboard.clearContents()
        pasteboard.writeObjects(urls.map { $0 as NSURL })
        pasteboard.setString("1", forType: Self.remoteMarker)
        lastChangeCount = pasteboard.changeCount
        Log.clipboard.info("Записаны в буфер файлы: \(urls.count) элементов")
    }

    private func poll() {
        let count = pasteboard.changeCount
        guard count != lastChangeCount else { return }
        lastChangeCount = count
        onExternalChange()
        guard shouldRead() else { return }

        let types = pasteboard.types ?? []
        if types.contains(Self.remoteMarker) {
            return
        }
        if types.contains(where: { Self.secretTypes.contains($0.rawValue) }) {
            Log.clipboard.info("Секретное содержимое (пароль) не передаётся")
            return
        }
        if types.contains(.fileURL) {
            guard shouldReadFiles() else {
                Log.clipboard.info("В буфере файлы, но принять их некому — ничего не отправляется")
                return
            }
            let urls = (pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? [])
                // Finder кладёт ссылки вида file:///.file/id=…; Swift обычно сам переводит их в пути, но на всякий случай.
                .map { ($0 as NSURL).filePathURL ?? $0 }
            guard !urls.isEmpty else { return }
            onCopyFiles(urls)
            return
        }
        let text = pasteboard.string(forType: .string)
        if shouldReadImages(), Self.textIsAuxiliary(text),
           // Первый по порядку в буфере — тот, что положила программа; остальные macOS выводит из него сама
           // (к PNG, например, добавляет TIFF), поэтому берём первый.
           let imageType = types.first(where: Self.imageTypes.contains),
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
