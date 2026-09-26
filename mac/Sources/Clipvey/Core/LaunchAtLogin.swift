import Observation
import ServiceManagement

/// Автозапуск при входе в систему через SMAppService (объекты входа macOS).
@MainActor
@Observable
final class LaunchAtLogin {
    static let shared = LaunchAtLogin()

    private(set) var status: SMAppService.Status = .notFound
    private(set) var lastError: String?

    private init() {
        refresh()
    }

    /// Работает только из .app; из голого исполняемого файла переключатель недоступен.
    var isAvailable: Bool { AppInfo.isBundled }

    /// Включён ли автозапуск. «Ждёт одобрения» тоже считаем включённым:
    /// пользователь его включил, осталось разрешить в Системных настройках.
    var isEnabled: Bool { status == .enabled || status == .requiresApproval }

    var requiresApproval: Bool { status == .requiresApproval }

    func refresh() {
        guard isAvailable else { return }
        status = SMAppService.mainApp.status
    }

    func setEnabled(_ enabled: Bool) {
        guard isAvailable else { return }
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            lastError = nil
        } catch {
            lastError = error.localizedDescription
            Log.app.error("Автозапуск (\(enabled ? "вкл" : "выкл", privacy: .public)): \(error.localizedDescription, privacy: .public)")
        }
        refresh()
        Log.app.notice("Автозапуск: статус \(String(describing: self.status), privacy: .public)")
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
