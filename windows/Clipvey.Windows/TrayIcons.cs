using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Состояние, которое показывает значок в трее.
internal enum TrayState
{
    /// Нет подключений (или нет связанных устройств): обычный значок.
    Idle,

    /// Есть хотя бы одно подключение: зелёная точка в углу.
    Connected,

    /// Синхронизация выключена со всеми связанными устройствами: серый перечёркнутый значок.
    AllDisabled,
}

/// Значки трея для трёх состояний. Рисуются во время работы из Clipvey.ico
/// под текущий размер значка в трее (SystemInformation.SmallIconSize, зависит от DPI).
/// Каждый значок держит свой HICON, его нужно освободить (DestroyIcon) — это делает Dispose.
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayState, (Icon Icon, IntPtr Handle)> _icons = [];
    private Size _size;

    public Icon Get(TrayState state)
    {
        var size = SystemInformation.SmallIconSize;
        if (size != _size)
        {
            // Сменился DPI: старые значки не выбрасываем сразу — один из них может ещё стоять в трее.
            // Вызывающий сначала ставит новый значок, потом зовёт ReleaseStale.
            _stale.AddRange(_icons.Values);
            _icons.Clear();
            _size = size;
        }
        if (_icons.TryGetValue(state, out var cached))
            return cached.Icon;

        var created = Create(state, size);
        _icons[state] = created;
        return created.Icon;
    }

    private readonly List<(Icon Icon, IntPtr Handle)> _stale = [];

    /// Освободить значки прежнего размера (после того как в трей поставлен новый).
    public void ReleaseStale()
    {
        foreach (var icon in _stale)
            Destroy(icon);
        _stale.Clear();
    }

    public void Dispose()
    {
        ReleaseStale();
        foreach (var icon in _icons.Values)
            Destroy(icon);
        _icons.Clear();
    }

    private static void Destroy((Icon Icon, IntPtr Handle) icon)
    {
        icon.Icon.Dispose();
        if (icon.Handle != IntPtr.Zero)
            DestroyIcon(icon.Handle);
    }

    private static (Icon Icon, IntPtr Handle) Create(TrayState state, Size size)
    {
        try
        {
            using var bitmap = Render(state, size);
            var handle = bitmap.GetHicon();
            // Icon.FromHandle не владеет HICON: освобождаем его сами в Destroy.
            return (Icon.FromHandle(handle), handle);
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось нарисовать значок трея ({state}, {size.Width}×{size.Height}): {e.Message}");
            return (AppIcon.Load(size), IntPtr.Zero);
        }
    }

    private static Bitmap Render(TrayState state, Size size)
    {
        var canvas = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        using var source = AppIcon.LoadBitmap(Math.Max(size.Width, size.Height));
        var bounds = new Rectangle(Point.Empty, size);
        if (state == TrayState.AllDisabled)
        {
            // Серый и полупрозрачный значок.
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix(
            [
                [0.30f, 0.30f, 0.30f, 0, 0],
                [0.59f, 0.59f, 0.59f, 0, 0],
                [0.11f, 0.11f, 0.11f, 0, 0],
                [0, 0, 0, 0.55f, 0],
                [0, 0, 0, 0, 1],
            ]));
            graphics.DrawImage(source, bounds, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);

            // Перечёркивание из угла в угол: светлая подложка, чтобы черта читалась на тёмной и светлой панели задач.
            var width = Math.Max(1.5f, size.Width / 8f);
            var inset = size.Width / 8f;
            var from = new PointF(inset, size.Height - inset);
            var to = new PointF(size.Width - inset, inset);
            using var halo = new Pen(Color.FromArgb(230, 255, 255, 255), width + 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var line = new Pen(Color.FromArgb(255, 200, 40, 40), width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(halo, from, to);
            graphics.DrawLine(line, from, to);
            return canvas;
        }

        graphics.DrawImage(source, bounds);
        if (state == TrayState.Connected)
        {
            // Зелёная точка в правом нижнем углу с белой обводкой.
            var diameter = Math.Max(6f, size.Width * 0.45f);
            var dot = new RectangleF(size.Width - diameter, size.Height - diameter, diameter, diameter);
            using var ring = new SolidBrush(Color.White);
            using var fill = new SolidBrush(Color.FromArgb(255, 22, 163, 74));
            graphics.FillEllipse(ring, dot);
            var border = Math.Max(1f, diameter / 6f);
            dot.Inflate(-border, -border);
            graphics.FillEllipse(fill, dot);
        }
        return canvas;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
