// Протокол (ClipveyProtocol) виден во всём приложении без отдельного import в каждом файле.
@_exported import ClipveyProtocol
import Foundation
import IOKit.pwr_mgt
import os

/// Журнал. Смотреть: log stream --predicate 'subsystem == "io.github.lovipomidorku.clipvey"' --level info
enum Log {
    static let subsystem = "io.github.lovipomidorku.clipvey"
    static let app = Logger(subsystem: subsystem, category: "app")
    static let network = Logger(subsystem: subsystem, category: "network")
    static let clipboard = Logger(subsystem: subsystem, category: "clipboard")
}

enum AppInfo {
    static var displayName: String {
        let info = Bundle.main.infoDictionary
        return info?["CFBundleDisplayName"] as? String ?? info?["CFBundleName"] as? String ?? "Clipvey"
    }

    /// Запущено ли из .app (автозапуск работает только так).
    static var isBundled: Bool { Bundle.main.bundleIdentifier != nil }

    /// Имя этого Mac, как его видят другие устройства.
    static var deviceName: String {
        Host.current().localizedName ?? ProcessInfo.processInfo.hostName
    }
}

/// Не даёт Mac засыпать от бездействия, пока активен (например, пока подключены другие устройства).
@MainActor
final class SleepGuard {
    private var assertionID: IOPMAssertionID = 0
    private(set) var isActive = false

    func setActive(_ active: Bool, reason: String) {
        if active && !isActive {
            let result = IOPMAssertionCreateWithName(
                kIOPMAssertionTypePreventUserIdleSystemSleep as CFString,
                IOPMAssertionLevel(kIOPMAssertionLevelOn),
                reason as CFString,
                &assertionID)
            isActive = result == kIOReturnSuccess
            Log.app.info("Запрет сна: \(self.isActive ? "включён" : "не удалось включить", privacy: .public)")
        } else if !active && isActive {
            IOPMAssertionRelease(assertionID)
            isActive = false
            Log.app.info("Запрет сна снят")
        }
    }
}

/// Настройки приложения в UserDefaults.
enum Settings {
    static var keepAwake: Bool {
        get { UserDefaults.standard.object(forKey: "keepAwake") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "keepAwake") }
    }

    static var language: AppLanguage {
        get { UserDefaults.standard.string(forKey: "language").flatMap(AppLanguage.init(rawValue:)) ?? .system }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: "language") }
    }
}
