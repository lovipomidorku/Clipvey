import AppKit

/// Мост между буфером обмена macOS и узлом Clipvey.
/// macOS не сообщает об изменениях буфера, поэтому changeCount проверяется дважды в секунду.
/// Само содержимое читается, только когда есть подключённые устройства.
@MainActor
final class PasteboardBridge {
    /// Отметка «текст пришёл с другого устройства»: такой текст не отправляется обратно.
    static let remoteMarker = NSPasteboard.PasteboardType("io.github.lovipomidorku.clipvey.remote")
    /// Так менеджеры паролей помечают секретное содержимое (nspasteboard.org).
    private static let secretTypes: Set<String> = ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType"]
    private static let maxBytes = 1024 * 1024

    private let pasteboard: NSPasteboard
    private var lastChangeCount: Int
    private var timer: Timer?

    var shouldRead: () -> Bool = { false }
    var onCopy: (String) -> Void = { _ in }

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
        guard let text = pasteboard.string(forType: .string), !text.isEmpty else { return }
        guard text.utf8.count <= Self.maxBytes else {
            Log.clipboard.notice("Текст больше 1 МиБ — не передаётся")
            return
        }
        onCopy(text)
    }
}
