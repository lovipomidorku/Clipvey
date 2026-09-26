// Протокол (ClipveyProtocol) виден во всём приложении без отдельного import в каждом файле.
@_exported import ClipveyProtocol
import Foundation
import IOKit.ps
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

    /// Тип этого Mac для значков на других устройствах: os=mac, form — laptop или desktop.
    static var deviceType: DeviceType {
        DeviceType(os: "mac", form: isLaptop ? "laptop" : "desktop")
    }

    /// Ноутбук: модель MacBook* (hw.model). Ноутбуки на Apple silicon с 2023 года называются Mac14,2 и т. п.,
    /// поэтому, если имя модели не подсказало, ноутбуком считается Mac со встроенным аккумулятором.
    private static var isLaptop: Bool {
        var size = 0
        if sysctlbyname("hw.model", nil, &size, nil, 0) == 0, size > 0 {
            var buffer = [CChar](repeating: 0, count: size)
            if sysctlbyname("hw.model", &buffer, &size, nil, 0) == 0,
               String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self).hasPrefix("MacBook") {
                return true
            }
        }
        return hasInternalBattery
    }

    private static var hasInternalBattery: Bool {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let sources = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef] else { return false }
        return sources.contains { source in
            let description = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any]
            return description?[kIOPSTypeKey] as? String == kIOPSInternalBatteryType
        }
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

    /// Своё имя устройства, заданное пользователем; nil — имя компьютера.
    static var deviceName: String? {
        get { UserDefaults.standard.string(forKey: "deviceName").flatMap { $0.isEmpty ? nil : $0 } }
        set { UserDefaults.standard.set(newValue, forKey: "deviceName") }
    }

    /// «Передавать картинки» (по умолчанию включено).
    static var imagesEnabled: Bool {
        get { UserDefaults.standard.object(forKey: "imagesEnabled") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "imagesEnabled") }
    }

    static var language: AppLanguage {
        get { UserDefaults.standard.string(forKey: "language").flatMap(AppLanguage.init(rawValue:)) ?? .system }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: "language") }
    }
}
