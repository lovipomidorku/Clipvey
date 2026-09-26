using System.Runtime.InteropServices;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Где поставить панель: в углу экрана у области уведомлений, со стороны панели задач.
internal static class PanelPlacement
{
    /// Отступ панели от края рабочей области (в пикселях при 96 DPI).
    private const int Margin = 12;

    /// Левый верхний угол панели размера size у панели задач на экране под курсором.
    public static Point Locate(Size size, int dpi)
    {
        var margin = Margin * dpi / 96;
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        var edge = TaskbarEdge(screen, ref area);

        // Как у всплывающих окон Windows 11: угол у области уведомлений.
        var x = edge == DockStyle.Left ? area.Left + margin : area.Right - size.Width - margin;
        var y = edge == DockStyle.Top ? area.Top + margin : area.Bottom - size.Height - margin;

        // Не выходить за рабочую область; если панель выше экрана — прижать к верху.
        x = Math.Max(area.Left, Math.Min(x, area.Right - size.Width));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height));
        return new Point(x, y);
    }

    /// Край, у которого стоит панель задач на этом экране. Если панель задач скрывается автоматически,
    /// рабочая область её не учитывает — тогда вычитаем её прямоугольник из area.
    private static DockStyle TaskbarEdge(Screen screen, ref Rectangle area)
    {
        var bounds = screen.Bounds;
        if (TaskbarRect() is { } taskbar && taskbar.IntersectsWith(bounds))
        {
            // uEdge из ABM_GETTASKBARPOS не документирован как результат — край выводим из прямоугольника.
            var wide = taskbar.Width >= taskbar.Height;
            var edge = wide
                ? (taskbar.Top <= bounds.Top ? DockStyle.Top : DockStyle.Bottom)
                : (taskbar.Left <= bounds.Left ? DockStyle.Left : DockStyle.Right);
            if (area.IntersectsWith(taskbar))
            {
                switch (edge)
                {
                    case DockStyle.Bottom:
                        area.Height = Math.Max(0, Math.Min(area.Bottom, taskbar.Top) - area.Top);
                        break;
                    case DockStyle.Top:
                        var top = Math.Max(area.Top, taskbar.Bottom);
                        area.Height = Math.Max(0, area.Bottom - top);
                        area.Y = top;
                        break;
                    case DockStyle.Left:
                        var left = Math.Max(area.Left, taskbar.Right);
                        area.Width = Math.Max(0, area.Right - left);
                        area.X = left;
                        break;
                    case DockStyle.Right:
                        area.Width = Math.Max(0, Math.Min(area.Right, taskbar.Left) - area.Left);
                        break;
                }
            }
            return edge;
        }

        // Панель задач на другом экране или неизвестна: смотрим, с какой стороны рабочая область меньше экрана.
        if (area.Top > bounds.Top)
            return DockStyle.Top;
        if (area.Left > bounds.Left)
            return DockStyle.Left;
        if (area.Right < bounds.Right)
            return DockStyle.Right;
        return DockStyle.Bottom;
    }

    private static Rectangle? TaskbarRect()
    {
        try
        {
            var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == UIntPtr.Zero)
                return null;
            return Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
        }
        catch (Exception e)
        {
            Log.Write($"Положение панели задач неизвестно: {e.Message}");
            return null;
        }
    }

    private const uint AbmGetTaskbarPos = 0x00000005;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
}

/// Оформление окна средствами DWM (Windows 11). На Windows 10 вызовы не срабатывают — это не ошибка.
internal static class Dwm
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// Скруглить углы окна. false — система не поддерживает (Windows 10).
    public static bool TryRoundCorners(IntPtr window)
    {
        try
        {
            var preference = DwmwcpRound;
            return DwmSetWindowAttribute(window, DwmwaWindowCornerPreference, ref preference, sizeof(int)) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
