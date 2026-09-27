using Microsoft.Win32;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Светлая или тёмная тема — по системной настройке «Режим приложений»
/// (HKCU\...\Themes\Personalize, AppsUseLightTheme). Следит за её сменой на ходу.
///
/// Цвета элементов WPF берёт из темы Windows 11 сам (Application.ThemeMode = System) и перекрашивает их
/// без нашего участия. Здесь тема нужна для того, что WPF не делает: тёмного фона окна у DWM
/// (подложка Acrylic) и журнала.
///
/// Для проверок: флаг --theme light|dark задаёт тему явно, не трогая системную настройку.
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static SynchronizationContext? _ui;

    /// Тема задана флагом --theme (null — как в системе).
    public static bool? Forced { get; private set; }

    public static bool IsDark { get; private set; }

    /// Тема сменилась. Вызывается на потоке интерфейса.
    public static event Action? Changed;

    /// Прочитать флаг --theme. Вызывается до создания окон.
    public static void Load(string[] args)
    {
        var index = Array.IndexOf(args, "--theme");
        Forced = index >= 0 && index + 1 < args.Length ? args[index + 1] switch
        {
            "dark" => true,
            "light" => false,
            _ => null,
        } : null;
        IsDark = Forced ?? ReadSystemIsDark();
    }

    /// Начать следить за системной настройкой. ui — контекст потока интерфейса.
    public static void Start(SynchronizationContext ui)
    {
        _ui = ui;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Log.Write($"Тема: {(IsDark ? "тёмная" : "светлая")}{(Forced is null ? "" : " (задана флагом --theme)")}");
    }

    public static void Stop() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    // Смена темы приходит как WM_SETTINGCHANGE "ImmersiveColorSet", обычно с категорией General.
    // Категорию не проверяем: перечитываем настройку и сообщаем, только если она изменилась.
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        _ui?.Post(_ => Recheck(), null);

    private static void Recheck()
    {
        if (Forced is not null)
            return;
        var dark = ReadSystemIsDark();
        if (dark == IsDark)
            return;
        IsDark = dark;
        Log.Write($"Тема сменилась: {(dark ? "тёмная" : "светлая")}");
        Changed?.Invoke();
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось прочитать тему Windows, беру светлую: {e.Message}");
            return false;
        }
    }
}
