using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clipvey.Core;
using static Clipvey.Windows.Localization;
using Forms = System.Windows.Forms;

namespace Clipvey.Windows;

/// Сколько получено из скольких.
internal readonly record struct ToastProgress(long Received, long Total);

/// Значок окошка.
internal enum ToastIcon
{
    File,
    Folder,
    Download,
    Done,
    Warning,
}

/// Что показать в окошке. Title — первая строка (Bold — жирное начало, например имя устройства; TitleStrong —
/// вся строка жирная), Detail — вторая, серая. Progress — полоса и «получено из всего · скорость» (опрашивается
/// 4 раза в секунду). Action — кнопка справа внизу (Accent — цвета акцента). Close — «×» в углу.
/// AutoHide — исчезнуть само через столько (пока мышь на окошке — ждёт), тогда вызывается Expired.
internal sealed record ToastView(ToastIcon Icon, string Title)
{
    public string? Bold { get; init; }
    public bool TitleStrong { get; init; }
    public string? Detail { get; init; }
    public Func<ToastProgress>? Progress { get; init; }
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public bool ActionAccent { get; init; }
    public Action? Close { get; init; }
    public TimeSpan? AutoHide { get; init; }
    public Action? Expired { get; init; }
}

/// Всплывающее окошко у области уведомлений: большие файлы («… скопировал …» и «Загрузить»), прогресс загрузки
/// (полоса, скорость, «Отмена»), «Готово» (исчезает само) и ошибки («Закрыть»). Что в нём — решает FileTransfers
/// (стопка сообщений, видно верхнее); окошко только рисует одно ToastView.
///
/// Не забирает фокус (ShowActivated = false, WS_EX_NOACTIVATE, MA_NOACTIVATE): окошко не уводит клавиатуру
/// из программы, в которой работает пользователь; клик по кнопке окошка тоже не делает его активным, поэтому
/// открытая панель не прячется. Стоит у области уведомлений; если открыта панель — над ней (или слева),
/// чтобы не закрывать её.
internal sealed partial class ToastWindow : Window
{
    /// Мышь ушла с окошка — исчезнуть не сразу, а через столько (вдруг вернётся).
    private static readonly TimeSpan HoverGrace = TimeSpan.FromSeconds(1.5);

    private readonly Func<System.Drawing.Rectangle?> _panelBounds;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _autoHide;
    private IntPtr _hwnd;
    private object? _key;
    private ToastView? _view;
    private readonly Queue<(long Time, long Bytes)> _samples = new();
    private bool _quitting;

    /// panelBounds — прямоугольник открытой панели в физических пикселях (null — панель не видна).
    public ToastWindow(Func<System.Drawing.Rectangle?> panelBounds)
    {
        _panelBounds = panelBounds;
        InitializeComponent();
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => UpdateProgress(), Dispatcher);
        _tick.Stop();
        _autoHide = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Normal, (_, _) => OnAutoHide(), Dispatcher);
        _autoHide.Stop();
        SourceInitialized += (_, _) => OnSourceInitialized();
        SizeChanged += (_, _) => Place();
        Theme.Changed += ApplyBackdrop;
    }

    /// Показать сообщение key. То же сообщение с новым видом (загрузка закончилась) — на месте, без мигания;
    /// прогресс и скорость при этом считаются заново, только если прогресса раньше не было.
    public void Show(object key, ToastView view)
    {
        var sameProgress = ReferenceEquals(_key, key) && _view?.Progress is not null && view.Progress is not null;
        _key = key;
        _view = view;
        SetIcon(view.Icon);

        TitleText.Inlines.Clear();
        if (view.Bold is { Length: > 0 } bold)
            TitleText.Inlines.Add(new System.Windows.Documents.Run(bold) { FontWeight = FontWeights.SemiBold });
        TitleText.Inlines.Add(new System.Windows.Documents.Run(view.Title));
        TitleText.FontWeight = view.TitleStrong ? FontWeights.SemiBold : FontWeights.Normal;
        // Под «×» — пустое место справа, чтобы текст на него не заезжал.
        TitleText.Margin = new Thickness(0, 0, view.Close is null ? 0 : 20, 0);
        System.Windows.Automation.AutomationProperties.SetName(TitleText, (view.Bold ?? "") + view.Title);

        DetailText.Text = view.Detail ?? "";
        DetailText.Visibility = view.Detail is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        Progress.Visibility = view.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = "";
        ActionButton.Content = view.ActionText;
        ActionButton.Visibility = view.ActionText is null ? Visibility.Collapsed : Visibility.Visible;
        if (view.ActionAccent)
            ActionButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
        else
            ActionButton.ClearValue(StyleProperty);
        Footer.Visibility = view.Progress is null && view.ActionText is null ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = view.Close is null ? Visibility.Collapsed : Visibility.Visible;
        var close = L("Закрыть", "Close");
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, close);
        CloseButton.ToolTip = close;

        if (view.Progress is null)
        {
            _tick.Stop();
        }
        else
        {
            if (!sameProgress)
                _samples.Clear();
            UpdateProgress();
            _tick.Start();
        }
        _autoHide.Stop();
        _hovered = false;
        if (view.AutoHide is { } lifetime)
        {
            _autoHide.Interval = lifetime;
            _autoHide.Start();
        }
        Present();
    }

    public void HideToast()
    {
        _tick.Stop();
        _autoHide.Stop();
        _key = null;
        _view = null;
        if (IsVisible)
            Hide();
    }

    /// Выход из программы.
    public void CloseToast()
    {
        _quitting = true;
        Theme.Changed -= ApplyBackdrop;
        _tick.Stop();
        _autoHide.Stop();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_quitting)
        {
            e.Cancel = true;
            if (_view?.Close is { } close)
                close();
            else
                HideToast();
        }
    }

    private void SetIcon(ToastIcon icon)
    {
        var (glyph, brush) = icon switch
        {
            ToastIcon.File => ("\uE8A5", "AccentTextFillColorPrimaryBrush"), // Document
            ToastIcon.Folder => ("\uE8B7", "AccentTextFillColorPrimaryBrush"), // Folder
            ToastIcon.Download => ("\uE896", "AccentTextFillColorPrimaryBrush"), // Download
            ToastIcon.Done => ("\uE73E", "SystemFillColorSuccessBrush"), // CheckMark
            _ => ("\uE7BA", "SystemFillColorCautionBrush"), // Warning
        };
        Symbol.Text = glyph;
        Symbol.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
    }

    /// «340 МБ из 1,2 ГБ · 45 МБ/с». Скорость — по последним ~3 с; меньше секунды данных — без скорости.
    private void UpdateProgress()
    {
        if (_view?.Progress is not { } poll)
            return;
        var progress = poll();
        var now = Environment.TickCount64;
        _samples.Enqueue((now, progress.Received));
        while (_samples.Count > 1 && now - _samples.Peek().Time > 3000)
            _samples.Dequeue();
        var (firstTime, firstBytes) = _samples.Peek();
        var speed = now - firstTime >= 1000 ? (progress.Received - firstBytes) * 1000.0 / (now - firstTime) : 0;
        Progress.Value = progress.Total > 0 ? Math.Min(1000, 1000.0 * progress.Received / progress.Total) : 0;
        var amount = L($"{UiText.Size(progress.Received)} из {UiText.Size(progress.Total)}",
            $"{UiText.Size(progress.Received)} of {UiText.Size(progress.Total)}");
        StatusText.Text = speed >= 1024 ? L($"{amount} · {UiText.Size((long)speed)}/с", $"{amount} · {UiText.Size((long)speed)}/s") : amount;
    }

    /// Пора исчезнуть. Пока мышь на окошке — ждём; ушла — ещё полторы секунды.
    private void OnAutoHide()
    {
        if (IsMouseInside())
        {
            _autoHide.Interval = TimeSpan.FromMilliseconds(300);
            _hovered = true;
            return;
        }
        if (_hovered)
        {
            _hovered = false;
            _autoHide.Interval = HoverGrace;
            return;
        }
        _autoHide.Stop();
        var expired = _view?.Expired;
        if (expired is null)
            HideToast();
        else
            expired();
    }

    private bool _hovered;

    private bool IsMouseInside() =>
        IsVisible && _hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var rect) && GetCursorPos(out var cursor)
        && cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;

    private void OnAction(object sender, RoutedEventArgs e) => _view?.Action?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e) => _view?.Close?.Invoke();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(IntPtr window, int index, nint value);
}
