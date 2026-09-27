using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clipvey.Core;
using static Clipvey.Windows.Localization;
using Forms = System.Windows.Forms;

namespace Clipvey.Windows;

/// Сколько получено из скольких и какой файл читается сейчас.
internal readonly record struct ToastProgress(long Received, long Total, string? Name);

/// Всплывающее окошко у области уведомлений: прогресс вставки больших файлов («Загрузка с …», полоса, скорость,
/// «Отмена»), «Готово» (исчезает само через 5 с) и ошибки («Закрыть»). Одно окошко за раз: новое сообщение
/// заменяет прежнее.
///
/// Не забирает фокус (ShowActivated = false, WS_EX_NOACTIVATE, MA_NOACTIVATE): вставка идёт в Проводнике,
/// и окошко не должно уводить из него клавиатуру; клик по кнопке окошка тоже не делает его активным, поэтому
/// открытая панель не прячется. Стоит у области уведомлений; если открыта панель — над ней (или слева),
/// чтобы не закрывать её.
internal sealed partial class ToastWindow : Window
{
    private static readonly TimeSpan DoneLifetime = TimeSpan.FromSeconds(5);

    private readonly Func<System.Drawing.Rectangle?> _panelBounds;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _autoHide;
    private IntPtr _hwnd;
    private object? _key;
    private Func<ToastProgress>? _poll;
    private Action? _action;
    private readonly Queue<(long Time, long Bytes)> _samples = new();
    private bool _quitting;

    /// panelBounds — прямоугольник открытой панели в физических пикселях (null — панель не видна).
    public ToastWindow(Func<System.Drawing.Rectangle?> panelBounds)
    {
        _panelBounds = panelBounds;
        InitializeComponent();
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => UpdateProgress(), Dispatcher);
        _tick.Stop();
        _autoHide = new DispatcherTimer(DoneLifetime, DispatcherPriority.Normal, (_, _) => HideToast(), Dispatcher);
        _autoHide.Stop();
        SourceInitialized += (_, _) => OnSourceInitialized();
        SizeChanged += (_, _) => Place();
        Theme.Changed += ApplyBackdrop;
    }

    /// Показано ли сейчас окошко этого дела (вставки, скачивания): чужое сообщение его уже заменило — не трогать.
    public bool Shows(object key) => IsVisible && ReferenceEquals(_key, key);

    /// Прогресс: заголовок, имя текущего файла, полоса, «получено из всего · скорость» и «Отмена».
    public void ShowProgress(object key, string title, Func<ToastProgress> poll, Action cancel)
    {
        _key = key;
        _poll = poll;
        _action = cancel;
        _autoHide.Stop();
        _samples.Clear();
        SetIcon("\uE896", "AccentTextFillColorPrimaryBrush"); // Download
        TitleText.Text = title;
        Progress.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        ActionButton.Content = L("Отмена", "Cancel");
        ActionButton.Visibility = Visibility.Visible;
        UpdateProgress();
        _tick.Start();
        Present();
    }

    /// «Готово»: исчезает само через 5 с.
    public void ShowDone(object? key, string title, string detail)
    {
        _key = key;
        StopProgress();
        SetIcon("\uE73E", "SystemFillColorSuccessBrush"); // CheckMark
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.TextWrapping = TextWrapping.Wrap;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        _autoHide.Stop();
        _autoHide.Start();
        Present();
    }

    /// Ошибка: короткий текст и «Закрыть».
    public void ShowError(object? key, string title, string detail)
    {
        _key = key;
        StopProgress();
        SetIcon("\uE7BA", "SystemFillColorCautionBrush"); // Warning
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = Visibility.Visible;
        DetailText.TextWrapping = TextWrapping.Wrap;
        Progress.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Visible;
        StatusText.Text = "";
        _action = HideToast;
        ActionButton.Content = L("Закрыть", "Close");
        ActionButton.Visibility = Visibility.Visible;
        _autoHide.Stop();
        Present();
    }

    public void HideToast()
    {
        StopProgress();
        _autoHide.Stop();
        _key = null;
        if (IsVisible)
            Hide();
    }

    /// Выход из программы.
    public void CloseToast()
    {
        _quitting = true;
        Theme.Changed -= ApplyBackdrop;
        StopProgress();
        _autoHide.Stop();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_quitting)
        {
            e.Cancel = true;
            HideToast();
        }
    }

    private void StopProgress()
    {
        _tick.Stop();
        _poll = null;
        _action = null;
    }

    private void SetIcon(string glyph, string brush)
    {
        Symbol.Text = glyph;
        Symbol.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
    }

    private void UpdateProgress()
    {
        if (_poll is not { } poll)
            return;
        var progress = poll();
        // Скорость — по последним ~3 с; данных за это время не было — скорость не показываем.
        var now = Environment.TickCount64;
        _samples.Enqueue((now, progress.Received));
        while (_samples.Count > 1 && now - _samples.Peek().Time > 3000)
            _samples.Dequeue();
        var (firstTime, firstBytes) = _samples.Peek();
        var speed = now - firstTime >= 1000 ? (progress.Received - firstBytes) * 1000.0 / (now - firstTime) : 0;
        Progress.Value = progress.Total > 0 ? Math.Min(1000, 1000.0 * progress.Received / progress.Total) : 0;
        DetailText.TextWrapping = TextWrapping.NoWrap;
        DetailText.TextTrimming = TextTrimming.CharacterEllipsis;
        DetailText.Text = progress.Name ?? L("Подготовка…", "Preparing…");
        DetailText.Visibility = Visibility.Visible;
        var amount = L($"{UiText.Size(progress.Received)} из {UiText.Size(progress.Total)}",
            $"{UiText.Size(progress.Received)} of {UiText.Size(progress.Total)}");
        StatusText.Text = speed >= 1024 ? L($"{amount} · {UiText.Size((long)speed)}/с", $"{amount} · {UiText.Size((long)speed)}/s") : amount;
    }

    private void OnAction(object sender, RoutedEventArgs e) => _action?.Invoke();

    // MARK: - Окно

    private void Present()
    {
        if (_hwnd == IntPtr.Zero)
            new WindowInteropHelper(this).EnsureHandle();
        UpdateLayout();
        Place();
        if (!IsVisible)
            Show();
        Place();
    }

    /// Панель открылась или спряталась — встать так, чтобы её не закрывать.
    public void Reposition()
    {
        if (IsVisible)
            Place();
    }

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        const int GwlStyle = -16;
        const int GwlExStyle = -20;
        const int WsExToolWindow = 0x00000080;
        const int WsExNoActivate = 0x08000000;
        const int WsSysMenu = 0x00080000;
        // Не в Alt+Tab и не становится активным даже по клику.
        SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        // Как у панели: рамка остаётся (по ней DWM рисует тень и скругление), кнопки закрытия нет.
        SetWindowLong(_hwnd, GwlStyle, GetWindowLong(_hwnd, GwlStyle) & ~WsSysMenu);
        if (HwndSource.FromHwnd(_hwnd) is { } source)
        {
            if (source.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
            source.AddHook(WndProc);
        }
        Dwm.TryRoundCorners(_hwnd);
        ApplyBackdrop();
    }

    /// Подложка — однотонный фон темы, а не Acrylic, как у панели: окошко никогда не становится активным,
    /// а у неактивного окна DWM всё равно рисует вместо Acrylic заливку (проверено на Windows 11 24H2).
    private void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Dwm.TrySetDark(_hwnd, Theme.IsDark);
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseActivate = 0x0021;
        const int MaNoActivate = 3;
        const int WmWindowPosChanging = 0x0046;
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }
        // Высота меняется вместе с содержимым: угол у панели задач остаётся на месте, без прыжка через кадр.
        if (message == WmWindowPosChanging)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            if ((pos.Flags & SwpNoSize) == 0 && pos.Width > 0 && pos.Height > 0)
            {
                var location = Locate(new System.Drawing.Size(pos.Width, pos.Height));
                pos.X = location.X;
                pos.Y = location.Y;
                pos.Flags &= ~SwpNoMove;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private void Place()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect))
            return;
        var location = Locate(new System.Drawing.Size(rect.Right - rect.Left, rect.Bottom - rect.Top));
        if (location.X == rect.Left && location.Y == rect.Top)
            return;
        SetWindowPos(_hwnd, IntPtr.Zero, location.X, location.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// Угол у области уведомлений основного экрана; при открытой панели — над ней, а если сверху нет места, слева.
    private System.Drawing.Point Locate(System.Drawing.Size size)
    {
        var dpi = _hwnd != IntPtr.Zero && GetDpiForWindow(_hwnd) is var windowDpi and > 0 ? (int)windowDpi : 96;
        var gap = 12 * dpi / 96;
        if (_panelBounds() is { } panel)
        {
            var area = Forms.Screen.FromRectangle(panel).WorkingArea;
            if (panel.Top - gap - size.Height >= area.Top)
                return new System.Drawing.Point(panel.Right - size.Width, panel.Top - gap - size.Height);
            return new System.Drawing.Point(Math.Max(area.Left, panel.Left - gap - size.Width), Math.Max(area.Top, panel.Bottom - size.Height));
        }
        return PanelPlacement.Locate(size, dpi, Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]);
    }

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(IntPtr window, int index, nint value);
}
