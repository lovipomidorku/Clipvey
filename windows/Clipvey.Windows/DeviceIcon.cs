using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Значок типа устройства (глиф Segoe Fluent Icons / Segoe MDL2 Assets) и, по желанию, точка состояния в углу.
/// Коды глифов есть в обоих шрифтах:
/// - E7F8 DeviceLaptopNoPic — ноутбук (Mac или ПК);
/// - E977 PC1 — настольный ПК с Windows;
/// - E7FB DeviceMonitorNoPic — настольный Mac (iMac, Mac mini, Mac Studio);
/// - E772 Devices — тип неизвестен (старая версия Clipvey или незнакомые os/form).
/// Если ни одного шрифта значков нет, рисуется только точка (без «квадратиков» вместо глифа).
internal sealed class DeviceIcon : Control
{
    private readonly string _glyph;
    private readonly Palette _palette;
    private readonly int _dpi;
    private readonly Color? _dot;

    public DeviceIcon(DeviceType? type, Palette palette, int dpi, Color background, Color? dot)
    {
        _glyph = Glyph(type);
        _palette = palette;
        _dpi = dpi;
        _dot = dot;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = background;
        Size = new Size(Scale(28), Scale(28));
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = Describe(type);
    }

    private int Scale(int pixels) => pixels * _dpi / 96;

    public static string Glyph(DeviceType? type) => (type?.Os, type?.Form) switch
    {
        (_, "laptop") => "",
        ("mac", _) => "",
        ("windows", _) => "",
        (_, "desktop") => "",
        _ => "",
    };

    /// Для экранного диктора: «Mac, ноутбук», «ПК с Windows» и т. п.
    public static string Describe(DeviceType? type)
    {
        var os = type?.Os switch
        {
            "mac" => "Mac",
            "windows" => L("ПК с Windows", "Windows PC"),
            _ => L("Устройство", "Device"),
        };
        return type?.Form switch
        {
            "laptop" => L($"{os}, ноутбук", $"{os}, laptop"),
            "desktop" => L($"{os}, настольный", $"{os}, desktop"),
            _ => os,
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var font = Theme.Icons(20, _dpi);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (font is null)
        {
            // Шрифта значков нет: одна точка по центру, как раньше.
            if (_dot is { } color)
            {
                var size = Scale(10);
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, (Width - size) / 2f, (Height - size) / 2f, size, size);
            }
            return;
        }
        TextRenderer.DrawText(g, _glyph, font, new Rectangle(0, 0, Width, Height), _palette.Text, BackColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (_dot is { } dot)
        {
            // Точка состояния в правом нижнем углу, с кольцом цвета фона — чтобы отделялась от глифа.
            var size = Scale(10);
            var ring = Math.Max(1, Scale(2));
            var x = Width - size - ring;
            var y = Height - size - ring;
            using (var back = new SolidBrush(BackColor))
                g.FillEllipse(back, x - ring, y - ring, size + ring * 2, size + ring * 2);
            using var brush = new SolidBrush(dot);
            g.FillEllipse(brush, x, y, size, size);
        }
    }
}
