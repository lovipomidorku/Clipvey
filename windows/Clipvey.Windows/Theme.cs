using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Цвета интерфейса. Близки к Windows 11 (Fluent): фон окна, карточки, акцентный синий.
internal sealed record Palette(
    bool IsDark,
    Color Background,
    Color Card,
    Color CardBorder,
    Color Text,
    Color SecondaryText,
    Color Accent,
    Color AccentText,
    Color AccentHover,
    Color Button,
    Color ButtonHover,
    Color ButtonPressed,
    Color ButtonBorder,
    Color Input,
    Color ToggleOff,
    Color Success,
    Color Warning,
    Color Danger,
    Color Disabled)
{
    public static readonly Palette Light = new(
        IsDark: false,
        Background: Color.FromArgb(243, 243, 243),
        Card: Color.FromArgb(255, 255, 255),
        CardBorder: Color.FromArgb(229, 229, 229),
        Text: Color.FromArgb(26, 26, 26),
        SecondaryText: Color.FromArgb(96, 96, 96),
        Accent: Color.FromArgb(0, 103, 192),
        AccentText: Color.White,
        AccentHover: Color.FromArgb(25, 117, 197),
        Button: Color.FromArgb(251, 251, 251),
        ButtonHover: Color.FromArgb(243, 243, 243),
        ButtonPressed: Color.FromArgb(235, 235, 235),
        ButtonBorder: Color.FromArgb(213, 213, 213),
        Input: Color.White,
        ToggleOff: Color.FromArgb(133, 133, 133),
        Success: Color.FromArgb(15, 123, 15),
        Warning: Color.FromArgb(157, 93, 0),
        Danger: Color.FromArgb(196, 43, 28),
        Disabled: Color.FromArgb(160, 160, 160));

    public static readonly Palette Dark = new(
        IsDark: true,
        Background: Color.FromArgb(32, 32, 32),
        Card: Color.FromArgb(43, 43, 43),
        CardBorder: Color.FromArgb(58, 58, 58),
        Text: Color.White,
        SecondaryText: Color.FromArgb(197, 197, 197),
        Accent: Color.FromArgb(76, 194, 255),
        AccentText: Color.Black,
        AccentHover: Color.FromArgb(71, 177, 232),
        Button: Color.FromArgb(55, 55, 55),
        ButtonHover: Color.FromArgb(62, 62, 62),
        ButtonPressed: Color.FromArgb(48, 48, 48),
        ButtonBorder: Color.FromArgb(72, 72, 72),
        Input: Color.FromArgb(30, 30, 30),
        ToggleOff: Color.FromArgb(160, 160, 160),
        Success: Color.FromArgb(108, 203, 95),
        Warning: Color.FromArgb(252, 225, 0),
        Danger: Color.FromArgb(255, 153, 164),
        Disabled: Color.FromArgb(110, 110, 110));
}

/// Светлая или тёмная тема — по системной настройке «Режим приложений»
/// (HKCU\...\Themes\Personalize, AppsUseLightTheme). Следит за её сменой на ходу.
///
/// Почему не Application.SetColorMode(SystemColorMode.System): в .NET 10 он уже не экспериментальный,
/// но панель почти целиком рисуется своими элементами, а стандартные элементы в тёмном режиме
/// WinForms выглядят по-разному. Своя палитра даёт одинаковый вид и переключение без перезапуска.
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Dictionary<(string Family, float Pixels, FontStyle Style), Font> Fonts = [];
    private static SynchronizationContext? _ui;
    private static string? _textFamily;
    private static string? _titleFamily;
    private static string? _monoFamily;

    public static Palette Current { get; private set; } = ReadSystemIsDark() ? Palette.Dark : Palette.Light;

    /// Тема сменилась. Вызывается на потоке интерфейса.
    public static event Action? Changed;

    /// Начать следить за системной настройкой. ui — контекст потока интерфейса.
    public static void Start(SynchronizationContext ui)
    {
        _ui = ui;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Log.Write($"Тема: {(Current.IsDark ? "тёмная" : "светлая")}");
    }

    public static void Stop() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    // Смена темы приходит как WM_SETTINGCHANGE "ImmersiveColorSet", обычно с категорией General.
    // Категорию не проверяем: перечитываем настройку и перестраиваемся, только если она изменилась.
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        _ui?.Post(_ => Recheck(), null);

    private static void Recheck()
    {
        var dark = ReadSystemIsDark();
        if (dark == Current.IsDark)
            return;
        Current = dark ? Palette.Dark : Palette.Light;
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

    // MARK: - Шрифты

    /// Основной шрифт: Segoe UI Variable (Windows 11), иначе Segoe UI. Размер — в пикселях при 96 DPI.
    public static Font Text(float pixels, int dpi, FontStyle style = FontStyle.Regular) =>
        Get(_textFamily ??= Pick("Segoe UI Variable Text", "Segoe UI"), pixels, dpi, style);

    /// Заголовки: Segoe UI Semibold, иначе основной шрифт полужирным.
    public static Font Title(float pixels, int dpi)
    {
        _titleFamily ??= Pick("Segoe UI Semibold", "");
        return _titleFamily.Length > 0
            ? Get(_titleFamily, pixels, dpi, FontStyle.Regular)
            : Text(pixels, dpi, FontStyle.Bold);
    }

    /// Моноширинный шрифт для кода связывания.
    public static Font Mono(float pixels, int dpi, FontStyle style = FontStyle.Regular) =>
        Get(_monoFamily ??= Pick("Cascadia Mono", Pick("Consolas", FontFamily.GenericMonospace.Name)), pixels, dpi, style);

    /// Шрифты кэшируются и не освобождаются: их немного, и они живут, пока работает программа.
    private static Font Get(string family, float pixels, int dpi, FontStyle style)
    {
        var size = pixels * dpi / 96f;
        var key = (family, size, style);
        if (!Fonts.TryGetValue(key, out var font))
        {
            font = new Font(family, size, style, GraphicsUnit.Pixel);
            Fonts[key] = font;
        }
        return font;
    }

    /// GDI+ молча подставляет другой шрифт, если нужного нет, — проверяем имя созданного.
    private static string Pick(string wanted, string fallback)
    {
        try
        {
            using var font = new Font(wanted, 12, GraphicsUnit.Pixel);
            if (string.Equals(font.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"Шрифт: {wanted}");
                return wanted;
            }
        }
        catch (Exception e)
        {
            Log.Write($"Шрифт {wanted} недоступен: {e.Message}");
        }
        Log.Write($"Шрифта {wanted} нет, беру {(fallback.Length > 0 ? fallback : "запасной")}");
        return fallback;
    }

    // MARK: - Рисование

    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// Тёмные полосы прокрутки у стандартного элемента (недокументированная тема Explorer; при сбое — как было).
    public static void ApplyScrollbarTheme(Control control)
    {
        try
        {
            if (control.IsHandleCreated)
                SetWindowTheme(control.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);
}
