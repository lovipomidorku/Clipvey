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
        MenuBarExtra(AppInfo.displayName, systemImage: "doc.on.clipboard") {
            RootView()
                .environment(appDelegate.model)
                .environment(LaunchAtLogin.shared)
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
    let startupError: String?

    var page: Page = .main
    /// Код, который пользователь вводит в роли I.
    var codeInput = ""
    /// Устройство, для которого показан вопрос «Разорвать связь?».
    var confirmUnpairID: String?

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
            node = ClipveyNode(identity: identity, name: AppInfo.deviceName, store: DeviceStore(directory: directory))
            startupError = nil
        } catch {
            node = nil
            startupError = "Не удалось загрузить ключ устройства: \(error.localizedDescription)"
        }
    }

    func start() {
        guard let node else { return }
        node.onClipReceived = { [weak self] text in self?.bridge.write(text) }
        node.onConnectionsChanged = { [weak self] _ in self?.updateSleepGuard() }
        bridge.shouldRead = { [weak node] in node?.devices.contains(where: \.connected) ?? false }
        bridge.onCopy = { [weak node] text in node?.broadcast(text) }
        TestHooks.prepare(node)
        node.start()
        bridge.start()
        TestHooks.run(node)
    }

    private func updateSleepGuard() {
        let connected = node?.devices.contains(where: \.connected) ?? false
        sleepGuard.setActive(keepAwake && connected, reason: "Clipvey: синхронизация буфера обмена с другими устройствами")
    }
}
