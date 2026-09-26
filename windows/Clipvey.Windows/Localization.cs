using System.Globalization;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Перевод интерфейса без файлов ресурсов: каждая строка записана сразу на двух языках,
/// L("Русский", "English"). Журнал не переводится — он всегда на русском.
internal static class Localization
{
    private static bool? _russian;

    /// Язык сменился: интерфейс нужно перестроить. Вызывается на потоке интерфейса.
    public static event Action? Changed;

    public static bool IsRussian => _russian ??= Resolve(AppSettings.Language);

    public static string L(string russian, string english) => IsRussian ? russian : english;

    public static AppLanguage Setting => AppSettings.Language;

    public static void Set(AppLanguage language)
    {
        if (language == AppSettings.Language)
            return;
        AppSettings.Language = language;
        _russian = Resolve(language);
        Log.Write($"Язык интерфейса: {language}");
        Changed?.Invoke();
    }

    private static bool Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Russian => true,
        AppLanguage.English => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru",
    };
}
