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
        Updater.shared.start()
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
    var confirmUnpairID: String? {
        didSet { if confirmUnpairID != nil { renamingDeviceID = nil } }
    }
    /// Устройство, которому сейчас задают псевдоним, и набранный текст.
    var renamingDeviceID: String? {
        didSet { if renamingDeviceID != nil { confirmUnpairID = nil } }
    }
    var aliasInput = ""
    /// Поле «Имя этого Mac» в настройках; пустое — имя компьютера.
    var nameInput = ""

    /// Последняя передача через буфер: когда, что и в какую сторону. Только в памяти, без содержимого.
    struct LastSync {
        enum Direction {
            case received(from: String)
            case sent
        }

        enum Kind {
            case text
            case image
        }

        let date: Date
        let direction: Direction
        let kind: Kind
    }

    private(set) var lastSync: LastSync?

    /// Что передано последним; nil — с запуска ничего не передавалось.
    var lastSyncKind: LastSync.Kind? { lastSync?.kind }

    /// «11:03 · от OFFICE-PC», «11:05 · отправлено», «11:07 · картинка от OFFICE-PC» или «11:09 · картинка отправлена»;
    /// nil — с запуска ничего не передавалось.
    var lastSyncText: String? {
        guard let lastSync else { return nil }
        let time = Calendar.current.isDateInToday(lastSync.date)
            ? lastSync.date.formatted(date: .omitted, time: .shortened)
            : lastSync.date.formatted(date: .abbreviated, time: .shortened)
        switch (lastSync.kind, lastSync.direction) {
        case (.text, .received(let from)):
            return L("\(time) · от \(from)", "\(time) · from \(from)")
        case (.text, .sent):
            return L("\(time) · отправлено", "\(time) · sent")
        case (.image, .received(let from)):
            return L("\(time) · картинка от \(from)", "\(time) · image from \(from)")
        case (.image, .sent):
            return L("\(time) · картинка отправлена", "\(time) · image sent")
        }
    }

    @ObservationIgnored let bridge: PasteboardBridge

    init() {
        let directory = TestHooks.dataDirectory
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Clipvey")
        let pasteboard = TestHooks.pasteboardName.map { NSPasteboard(name: NSPasteboard.Name($0)) } ?? .general
        bridge = PasteboardBridge(pasteboard: pasteboard)
        // Настройка «Не давать Mac засыпать» убрана — стираем её след.
        UserDefaults.standard.removeObject(forKey: "keepAwake")
        do {
            let identity = try DeviceIdentity.loadOrCreate(at: directory.appendingPathComponent("identity.key"))
            node = ClipveyNode(
                identity: identity,
                name: TestHooks.deviceName ?? Settings.deviceName ?? AppInfo.deviceName,
                deviceType: AppInfo.deviceType,
                imagesEnabled: TestHooks.enabled ? TestHooks.imagesEnabled : Settings.imagesEnabled,
                store: DeviceStore(directory: directory))
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
            self?.lastSync = LastSync(date: Date(), direction: .received(from: from), kind: .text)
        }
        node.onImageReceived = { [weak self] data, mime, from in
            self?.bridge.writeImage(data, mime: mime)
            self?.lastSync = LastSync(date: Date(), direction: .received(from: from), kind: .image)
        }
        bridge.shouldRead = { [weak node] in node?.devices.contains(where: \.connected) ?? false }
        bridge.shouldReadImages = { [weak node] in
            guard let node, node.imagesEnabled else { return false }
            return node.devices.contains { $0.connected && $0.enabled && $0.acceptsImages }
        }
        bridge.onCopyImage = { [weak self, weak node] data, mime in
            guard let node else { return }
            if node.sendImage(data, mime: mime) > 0 {
                self?.lastSync = LastSync(date: Date(), direction: .sent, kind: .image)
            }
        }
        bridge.onCopy = { [weak self, weak node] text in
            guard let node else { return }
            // Узел шлёт только подключённым включённым устройствам; если их нет, отправки не было.
            let hasRecipients = node.devices.contains { $0.connected && $0.enabled }
            node.broadcast(text)
            if hasRecipients {
                self?.lastSync = LastSync(date: Date(), direction: .sent, kind: .text)
            }
        }
        TestHooks.prepare(node)
        node.start()
        bridge.start()
        TestHooks.run(node)
        TestHooks.probe(self)
        observeTooltip()
    }

    // MARK: - Имя и картинки (настройки хранит приложение, узел только применяет)

    /// Сменить своё имя: сохранить и передать узлу (TXT, Bonjour, info всем сеансам). Пустое — имя компьютера.
    func rename(_ newName: String) {
        let normalized = ClipveyNode.normalizedName(newName)
        if !TestHooks.enabled {
            Settings.deviceName = normalized.isEmpty ? nil : normalized
        }
        node?.setName(normalized.isEmpty ? AppInfo.deviceName : normalized)
    }

    /// Заполнить поле «Имя этого Mac»: своё имя, если оно задано, иначе пусто (подсказкой видно имя компьютера).
    /// Берётся из узла, а не из UserDefaults: в режиме проверки имя не сохраняется.
    func loadNameInput() {
        guard let node else { return }
        nameInput = node.name == AppInfo.deviceName ? "" : node.name
    }

    /// Сохранить поле «Имя этого Mac», если оно изменилось.
    func commitNameInput() {
        guard let node else { return }
        var normalized = ClipveyNode.normalizedName(nameInput)
        if normalized == AppInfo.deviceName {
            normalized = ""
        }
        let current = node.name == AppInfo.deviceName ? "" : node.name
        nameInput = normalized
        guard normalized != current else { return }
        rename(normalized)
    }

    /// Открыть поле псевдонима у устройства (псевдоним хранится только на этом Mac).
    func beginRename(_ device: ClipveyNode.DeviceStatus) {
        aliasInput = device.alias ?? device.name
        renamingDeviceID = device.id
    }

    /// Сохранить псевдоним. Пустой или совпадающий с именем устройства — сбросить.
    func commitAlias(for device: ClipveyNode.DeviceStatus) {
        let alias = ClipveyNode.normalizedName(aliasInput)
        node?.setAlias(alias == device.name ? "" : alias, deviceID: device.id)
        renamingDeviceID = nil
    }

    /// Вернуть имя, которое устройство сообщает само.
    func resetAlias(for device: ClipveyNode.DeviceStatus) {
        node?.setAlias("", deviceID: device.id)
        renamingDeviceID = nil
    }

    /// «Передавать картинки».
    var imagesEnabled: Bool {
        get { node?.imagesEnabled ?? Settings.imagesEnabled }
        set {
            if !TestHooks.enabled {
                Settings.imagesEnabled = newValue
            }
            node?.setImagesEnabled(newValue)
        }
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
}
