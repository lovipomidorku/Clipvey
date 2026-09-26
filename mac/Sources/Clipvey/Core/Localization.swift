import Foundation
import Observation

/// Язык интерфейса по настройке «Язык».
enum AppLanguage: String, CaseIterable, Identifiable {
    /// Как в системе: русский, если первый предпочитаемый язык — русский, иначе английский.
    case system
    case russian = "ru"
    case english = "en"

    var id: String { rawValue }

    /// Название пункта в настройках. Языки названы на самих себе, как принято в macOS.
    @MainActor
    var title: String {
        switch self {
        case .system: L("Как в системе", "System default")
        case .russian: "Русский"
        case .english: "English"
        }
    }
}

/// Текущий язык интерфейса. Наблюдаемый: при смене SwiftUI сразу перерисовывает окно и значок.
@MainActor
@Observable
final class Language {
    static let shared = Language()

    var choice: AppLanguage {
        didSet { Settings.language = choice }
    }

    private init() {
        choice = Settings.language
    }

    var isRussian: Bool {
        switch choice {
        case .russian: true
        case .english: false
        case .system: Locale.preferredLanguages.first?.lowercased().hasPrefix("ru") ?? false
        }
    }
}

/// Строка интерфейса на текущем языке: L("Настройки", "Settings").
/// Журнал (Log) не переводится и остаётся на русском.
@MainActor
func L(_ russian: String, _ english: String) -> String {
    Language.shared.isRussian ? russian : english
}
