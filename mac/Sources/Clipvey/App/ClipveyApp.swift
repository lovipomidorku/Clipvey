import AppKit
import SwiftUI

@main
enum Entry {
    @MainActor
    static func main() {
        TestHooks.parse(Array(CommandLine.arguments.dropFirst()))
        ClipveyApp.main()
    }
}

struct ClipveyApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
            RootView()
                .environment(appDelegate.model)
                .environment(LaunchAtLogin.shared)
        } label: {
            MenuBarLabel(model: appDelegate.model)
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = AppModel()

    func applicationDidFinishLaunching(_ notification: Notification) {
        model.start()
    }
}

/// Долгоживущее состояние приложения: узел, мост к буферу и настройки.
@MainActor
@Observable
final class AppModel {
    enum Page {
        case main
        case settings
    }

    let node: ClipveyNode?
    /// Почему узел не запустился (текст ошибки системы; подпись к нему подбирает интерфейс).
    let startupError: String?

    var page: Page = .main
    /// Код, который пользователь вводит в роли I.
    var codeInput = ""
    /// Устройство, для которого показан вопрос «Разорвать связь?».
    var confirmUnpairID: String?

    /// Последняя передача через буфер: когда и в какую сторону. Только в памяти, без содержимого.
    struct LastSync {
        enum Direction {
            case received(from: String)
            case sent
        }

        let date: Date
        let direction: Direction
    }

    private(set) var lastSync: LastSync?

    /// «11:03 · от OFFICE-PC» или «11:05 · отправлено»; nil — с запуска ничего не передавалось.
    var lastSyncText: String? {
        guard let lastSync else { return nil }
        let time = Calendar.current.isDateInToday(lastSync.date)
            ? lastSync.date.formatted(date: .omitted, time: .shortened)
            : lastSync.date.formatted(date: .abbreviated, time: .shortened)
        switch lastSync.direction {
        case .received(let from):
            return L("\(time) · от \(from)", "\(time) · from \(from)")
        case .sent:
            return L("\(time) · отправлено", "\(time) · sent")
        }
    }

    var keepAwake: Bool {
        didSet {
            Settings.keepAwake = keepAwake
            updateSleepGuard()
        }
    }

    @ObservationIgnored let bridge: PasteboardBridge
    @ObservationIgnored private let sleepGuard = SleepGuard()

    init() {
        let directory = TestHooks.dataDirectory
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Clipvey")
        let pasteboard = TestHooks.pasteboardName.map { NSPasteboard(name: NSPasteboard.Name($0)) } ?? .general
        bridge = PasteboardBridge(pasteboard: pasteboard)
        keepAwake = Settings.keepAwake
        do {
            let identity = try DeviceIdentity.loadOrCreate(at: directory.appendingPathComponent("identity.key"))
            node = ClipveyNode(identity: identity, name: TestHooks.deviceName ?? AppInfo.deviceName, store: DeviceStore(directory: directory))
            startupError = nil
        } catch {
            node = nil
            startupError = error.localizedDescription
            Log.app.error("Не удалось загрузить ключ устройства: \(error.localizedDescription, privacy: .public)")
        }
    }

    func start() {
        guard let node else { return }
        node.onClipReceived = { [weak self] text, from in
            self?.bridge.write(text)
            self?.lastSync = LastSync(date: Date(), direction: .received(from: from))
        }
        node.onConnectionsChanged = { [weak self] _ in self?.updateSleepGuard() }
        bridge.shouldRead = { [weak node] in node?.devices.contains(where: \.connected) ?? false }
        bridge.onCopy = { [weak self, weak node] text in
            guard let node else { return }
            // Узел шлёт только подключённым включённым устройствам; если их нет, отправки не было.
            let hasRecipients = node.devices.contains { $0.connected && $0.enabled }
            node.broadcast(text)
            if hasRecipients {
                self?.lastSync = LastSync(date: Date(), direction: .sent)
            }
        }
        TestHooks.prepare(node)
        node.start()
        bridge.start()
        TestHooks.run(node)
        observeTooltip()
    }

    // MARK: - Значок в строке меню

    var syncState: SyncState {
        guard let devices = node?.devices, !devices.isEmpty else { return .idle }
        if devices.contains(where: \.connected) { return .connected }
        if devices.allSatisfy({ !$0.enabled }) { return .disabled }
        return .idle
    }

    /// Состояние одной строкой: подсказка при наведении на значок.
    var statusSummary: String {
        let devices = node?.devices ?? []
        let connected = devices.filter(\.connected).count
        let state: String
        switch syncState {
        case .connected:
            state = L("подключено \(connected) из \(devices.count)", "\(connected) of \(devices.count) connected")
        case .disabled:
            state = L("синхронизация выключена", "sync is off")
        case .idle:
            state = devices.isEmpty
                ? L("нет связанных устройств", "no paired devices")
                : L("нет подключений", "not connected")
        }
        return "\(AppInfo.displayName) — \(state)"
    }

    @ObservationIgnored private var tooltip = ""
    @ObservationIgnored private var tooltipAttempts = 0

    /// Обновляет подсказку при каждом изменении того, из чего она складывается.
    private func observeTooltip() {
        tooltip = withObservationTracking {
            guard let lastSyncText else { return statusSummary }
            return "\(statusSummary)\n\(L("Последняя синхронизация", "Last sync")): \(lastSyncText)"
        } onChange: { [weak self] in
            Task { @MainActor in self?.observeTooltip() }
        }
        tooltipAttempts = 0
        applyTooltip()
    }

    /// Кнопка значка появляется не сразу после запуска: несколько раз пробуем ещё.
    private func applyTooltip() {
        if StatusItemTooltip.set(tooltip) {
            return
        }
        tooltipAttempts += 1
        guard tooltipAttempts <= 10 else {
            Log.app.info("Подсказка значка: кнопка в строке меню не найдена")
            return
        }
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            self?.applyTooltip()
        }
    }

    private func updateSleepGuard() {
        let connected = node?.devices.contains(where: \.connected) ?? false
        sleepGuard.setActive(keepAwake && connected, reason: "Clipvey: синхронизация буфера обмена с другими устройствами")
    }
}
