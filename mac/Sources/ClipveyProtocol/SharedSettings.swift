import Foundation

/// Общие настройки (docs/protocol.md, «Общие настройки»): меняются на одном устройстве — применяются на всех.
/// autoDownloadMB — до какого размера (МиБ) полученные файлы скачиваются тихо, в фоне; больше — по действию пользователя.
/// changed — когда настройки изменил пользователь (мс с 1970-01-01 UTC, 0 — не менял), by — deviceId устройства,
/// на котором их изменили. Совпадает с SharedSettings в Clipvey.Core на C#.
public struct SharedSettings: Equatable, Codable, Sendable, CustomStringConvertible {
    public var autoDownloadMB: Int
    public var changed: Int64
    public var by: String

    /// Допустимые значения autoDownloadMB по возрастанию. 10240 — «всегда»: весь допустимый размер описания (10 ГиБ).
    public static let allowedMB = [50, 100, 300, 500, 1000, 10240]
    public static let defaultMB = 50
    /// «Всегда»: тихо скачивается всё, что вообще можно передать.
    public static let alwaysMB = 10240

    public init(autoDownloadMB: Int, changed: Int64, by: String) {
        self.autoDownloadMB = autoDownloadMB
        self.changed = changed
        self.by = by
    }

    /// Пока пользователь ничего не менял: 50 МиБ, changed = 0, by = свой deviceId.
    public static func `default`(deviceID: String) -> SharedSettings {
        SharedSettings(autoDownloadMB: defaultMB, changed: 0, by: deviceID)
    }

    /// Ближайшее допустимое значение; при равном расстоянии — меньшее. Отрицательное и 0 — 50.
    public static func nearest(_ mb: Int) -> Int {
        nearest(Double(mb))
    }

    /// То же для любого числа из сообщения (в том числе дробного и огромного); не число — 50.
    public static func nearest(_ mb: Double) -> Int {
        guard !mb.isNaN else { return defaultMB }
        var best = allowedMB[0]
        for allowed in allowedMB where abs(mb - Double(allowed)) < abs(mb - Double(best)) {
            best = allowed
        }
        return best
    }

    /// Порог в байтах: полученные файлы всего не больше него скачиваются тихо.
    public var autoDownloadBytes: Int64 { Int64(autoDownloadMB) * 1_048_576 }

    /// Эти настройки новее other: большее changed, при равном — большее by (сравнение по кодовым единицам UTF-16,
    /// как string.CompareOrdinal в C#).
    public func isNewer(than other: SharedSettings) -> Bool {
        if changed != other.changed {
            return changed > other.changed
        }
        return other.by.utf16.lexicographicallyPrecedes(by.utf16)
    }

    /// Значение приведено к допустимому (nearest), changed не меньше 0.
    public func normalized() -> SharedSettings {
        SharedSettings(autoDownloadMB: Self.nearest(autoDownloadMB), changed: max(0, changed), by: by)
    }

    /// Разбор settings. Поля не обязательны (старые и будущие версии): autoDownloadMB — любое число, приводится
    /// к ближайшему допустимому, нет или не число — 50; changed — целое от 0 до 9·10^15, иначе 0; by — строка, иначе "".
    /// Настройки без changed и by никогда не новее своих: так пустое сообщение ничего не меняет.
    static func parse(_ object: [String: Any]) -> SharedSettings {
        var mb = defaultMB
        if let number = object["autoDownloadMB"] as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() {
            mb = nearest(number.doubleValue)
        }
        let changed = JSONNumbers.integer(object["changed"]).flatMap { $0 > 0 ? $0 : nil } ?? 0
        return SharedSettings(autoDownloadMB: mb, changed: changed, by: object["by"] as? String ?? "")
    }

    /// Для журнала и событий самопроверки: «300 1790000000000 <by>».
    public var description: String { "\(autoDownloadMB) \(changed) \(by)" }
}
