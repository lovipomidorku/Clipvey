using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Clipvey.Core;
using static Clipvey.Windows.Localization;
using Forms = System.Windows.Forms;

namespace Clipvey.Windows;

/// Что передано: текст или картинка.
internal enum SyncKind
{
    Text,
    Image,
}

/// Последняя синхронизация: когда, откуда (From — имя устройства; null — отправлено отсюда) и что.
/// Только в памяти, без содержимого.
internal sealed record LastSync(DateTime Time, string? From, SyncKind Kind);

/// Приложение без главного окна: значок в трее, узел Clipvey и слежение за буфером.
/// Значок — NotifyIcon из WinForms, меню значка и панель — WPF.
internal sealed class TrayApplication
{
    /// Предел длины подсказки у значка в трее (NotifyIcon.Text бросает исключение на длинной строке).
    private const int MaxTrayText = 63;

    private readonly ClipveyNode _node;
    private readonly ClipboardWatcher _watcher;
    private readonly Forms.NotifyIcon _tray;
    private readonly TrayIcons _icons = new();
    private TrayState? _trayState;
    private readonly SynchronizationContext _ui;
    private PanelWindow? _panel;
    private readonly Updater _updater;

    /// Меню значка. Строится при каждом открытии — так в нём всегда текущие язык, автозапуск и состояние.
    private ContextMenu? _menu;

    /// Невидимое окно, которое становится активным на время меню значка: без активного окна
    /// меню не закрывается по клику мимо.
    private HwndSource? _menuOwner;

    /// Последняя синхронизация (null — ещё не было).
    public LastSync? LastSync { get; private set; }

    /// Когда закроется режим связывания, открытый отсюда (для обратного отсчёта). Узел срок наружу не отдаёт.
    public DateTime? PairingDeadline { get; private set; }

    public ClipveyNode Node => _node;
    public Updater Updater => _updater;

    public TrayApplication()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Нет контекста потока интерфейса");
        var identity = Identity.LoadOrCreate(new DpapiSecretStore(AppPaths.DataDirectory));
        // Тип устройства: ноутбук, если есть батарея.
        var form = Forms.SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) ? "desktop" : "laptop";
        _node = new ClipveyNode(identity, new DeviceStore(AppPaths.DataDirectory), AppSettings.DeviceName ?? Environment.MachineName,
            deviceType: new DeviceType("windows", form), imagesEnabled: AppSettings.ImagesEnabled);
        _watcher = new ClipboardWatcher(OnLocalCopy, OnLocalImage,
            shouldRead: () => _node.Devices.Any(device => device.Connected),
            shouldReadImages: () => _node.ImagesEnabled && _node.Devices.Any(device => device.Connected && device.Enabled && device.AcceptsImages));

        _tray = new Forms.NotifyIcon
        {
            Icon = _icons.Get(TrayState.Idle),
            Text = "Clipvey",
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                TogglePanel();
        };
        // Меню — по отпусканию правой кнопки, как у значков Windows.
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
                OnUi(ShowMenu);
        };
        Localization.Changed += OnLanguageChanged;
        Theme.Start(_ui);

        _node.ClipReceived += (text, from) => OnUi(() =>
        {
            _watcher.WriteRemote(text);
            NoteSync(new LastSync(DateTime.Now, from, SyncKind.Text));
        });
        _node.ImageReceived += (data, mime, from) => OnUi(() =>
        {
            _watcher.WriteRemoteImage(data, mime);
            NoteSync(new LastSync(DateTime.Now, from, SyncKind.Image));
        });
        _node.Changed += () => OnUi(Refresh);
        _node.IncomingPairingChanged += incoming => OnUi(() =>
        {
            if (incoming is not null)
                ShowPanel(settings: true);
            Refresh();
        });
        _node.PairingSucceeded += name => OnUi(() =>
        {
            _panel?.ShowResult(L($"Связано с «{name}»", $"Paired with “{name}”"));
            Refresh();
        });
        _node.PairingFailed += reason => OnUi(() =>
        {
            _panel?.ShowResult(UiText.Failure(reason));
            Refresh();
        });

        _node.Start();
        Refresh();

        _updater = new Updater(() => OnUi(Quit));
        _updater.Changed += () => OnUi(() => _panel?.RefreshContent());
        _updater.UpdateFound += version => OnUi(() => _tray.ShowBalloonTip(15000, "Clipvey",
            L($"Доступна версия {version} — обновить?", $"Version {version} is available. Update?"), Forms.ToolTipIcon.Info));
        _tray.BalloonTipClicked += (_, _) => OnUi(ShowPanel);
        _updater.Start();
        ListenForShowPanel();
    }

    /// Сменить своё имя: сохранить и передать узлу. Пустое — имя компьютера.
    public void Rename(string newName)
    {
        var normalized = ClipveyNode.NormalizeName(newName);
        AppSettings.DeviceName = normalized.Length > 0 ? normalized : null;
        _node.SetName(normalized.Length > 0 ? normalized : Environment.MachineName);
    }

    /// «Передавать картинки»: сохранить и передать узлу.
    public void SetImagesEnabled(bool enabled)
    {
        AppSettings.ImagesEnabled = enabled;
        _node.SetImagesEnabled(enabled);
    }

    /// Открыть режим связывания (из панели и из меню значка) и запомнить срок для обратного отсчёта.
    public void StartPairingMode()
    {
        PairingDeadline = DateTime.UtcNow + Protocol.PairingTimeout;
        _node.StartPairingMode();
    }

    /// Варианты настройки «Язык». Названия языков пишутся на самих языках.
    public static IEnumerable<(AppLanguage Value, string Title)> LanguageChoices() =>
    [
        (AppLanguage.System, L("Как в системе", "Use system setting")),
        (AppLanguage.Russian, "Русский"),
        (AppLanguage.English, "English"),
    ];

    // MARK: - Меню значка

    private void ShowMenu()
    {
        if (_menu is { IsOpen: true })
            return;
        var devices = _node.Devices;
        var connected = devices.Count(device => device.Connected);
        var status = devices.Count == 0
            ? L("Нет связанных устройств", "No paired devices")
            : L($"Подключено {connected} из {devices.Count}", $"{connected} of {devices.Count} connected");

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = $"Clipvey — {status}", IsEnabled = false });
        menu.Items.Add(new Separator());
        // Панель открываем после закрытия меню: иначе меню, закрываясь, заберёт активность, и панель спрячется.
        menu.Items.Add(Item(L("Открыть Clipvey", "Open Clipvey"), () => AfterMenu(ShowPanel)));
        menu.Items.Add(Item(L("Добавить устройство", "Add Device…"), () => AfterMenu(() =>
        {
            StartPairingMode();
            ShowPanel(settings: true);
        })));
        menu.Items.Add(new Separator());

        var autostart = new MenuItem
        {
            Header = L("Запускать при входе в Windows", "Start with Windows"),
            IsCheckable = true,
            IsChecked = Autostart.SafeIsEnabled(),
        };
        autostart.Click += (_, _) => Autostart.SafeSet(autostart.IsChecked);
        menu.Items.Add(autostart);

        var language = new MenuItem { Header = L("Язык", "Language") };
        foreach (var (value, title) in LanguageChoices())
        {
            var choice = value;
            // Смену языка откладываем до закрытия меню: она перестраивает панель.
            language.Items.Add(Item(title, () => AfterMenu(() => Localization.Set(choice)), isChecked: Setting == value));
        }
        // У пункта с подменю в теме Windows 11 нет колонки для галочки — сдвигаем текст вровень с остальными пунктами.
        language.Loaded += (_, _) => language.Padding = new Thickness(language.Padding.Left + CheckColumnWidth,
            language.Padding.Top, language.Padding.Right, language.Padding.Bottom);
        menu.Items.Add(language);
        menu.Items.Add(Item(L("Открыть журнал", "Open log"), FileLog.Open));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L("Выход", "Quit"), () => AfterMenu(Quit)));

        menu.Opened += (_, _) =>
        {
            // Меню должно быть в активном окне, иначе клик мимо его не закроет.
            _menuOwner ??= new HwndSource(new HwndSourceParameters("Clipvey.Menu")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, невидимое
                ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW: не в Alt+Tab
            });
            SetForegroundWindow(_menuOwner.Handle);
            menu.Focus();
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
                _menu = null;
        };
        _menu = menu;
        menu.IsOpen = true;
    }

    /// Ширина колонки галочки в меню темы Windows 11 (DIP).
    private const double CheckColumnWidth = 26;

    private static MenuItem Item(string header, Action onClick, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void AfterMenu(Action action) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, action);

    // MARK: - Состояние

    private void OnLanguageChanged() => Refresh();

    private void OnLocalCopy(string text)
    {
        if (!_node.Devices.Any(device => device.Connected))
            return;
        _ = _node.BroadcastClipAsync(text);
        // «Отправлено» — приблизительно: ошибки отправки узел пишет в журнал, но наружу не сообщает.
        // Слишком длинный текст узел не отправляет — его не отмечаем.
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= Protocol.MaxClipBytes)
            NoteSync(new LastSync(DateTime.Now, From: null, SyncKind.Text));
    }

    private void OnLocalImage(byte[] data, string mime)
    {
        if (_node.SendImage(data, mime) > 0)
            NoteSync(new LastSync(DateTime.Now, From: null, SyncKind.Image));
    }

    private void NoteSync(LastSync sync)
    {
        LastSync = sync;
        _panel?.RefreshContent();
    }

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

    private void Refresh()
    {
        var devices = _node.Devices;
        var connected = devices.Count(device => device.Connected);
        var status = devices.Count == 0
            ? L("Нет связанных устройств", "No paired devices")
            : L($"Подключено {connected} из {devices.Count}", $"{connected} of {devices.Count} connected");
        if (devices.Count > 0 && devices.All(device => !device.Enabled))
            status = L("Синхронизация выключена", "Sync is off");
        UpdateTrayIcon(connected > 0 ? TrayState.Connected
            : devices.Count > 0 && devices.All(device => !device.Enabled) ? TrayState.AllDisabled
            : TrayState.Idle);
        var tip = $"Clipvey: {status}";
        _tray.Text = tip.Length <= MaxTrayText ? tip : tip[..(MaxTrayText - 1)] + "…";
        _panel?.RefreshContent();
    }

    private void UpdateTrayIcon(TrayState state)
    {
        var icon = _icons.Get(state);
        if (_trayState == state && ReferenceEquals(_tray.Icon, icon))
            return;
        if (_trayState != state)
            Log.Write($"Значок трея: {state}, {icon.Width}×{icon.Height}");
        _trayState = state;
        _tray.Icon = icon;
        _icons.ReleaseStale();
    }

    // MARK: - Повторный запуск

    /// Имя события «открой панель»: его взводит повторный запуск Clipvey.exe.
    private const string ShowPanelEventName = @"Local\Clipvey.ShowPanel";
    private EventWaitHandle? _showPanelEvent;
    private RegisteredWaitHandle? _showPanelWait;

    /// Для повторного запуска: попросить работающую копию открыть панель. false — копия старая и события не знает.
    public static bool SignalShowPanel()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowPanelEventName);
            // Разрешить работающей копии вывести панель на передний план.
            AllowSetForegroundWindow(-1);
            signal.Set();
            return true;
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ListenForShowPanel()
    {
        try
        {
            _showPanelEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowPanelEventName);
            _showPanelWait = ThreadPool.RegisterWaitForSingleObject(_showPanelEvent,
                (_, _) => OnUi(ShowPanel), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось подписаться на повторный запуск: {e.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    // MARK: - Панель

    private void ShowPanel() => ShowPanel(settings: false);

    private void ShowPanel(bool settings)
    {
        _panel ??= new PanelWindow(this);
        _panel.ShowPanel(settings);
    }

    /// Левый клик по значку: открыть панель или закрыть открытую.
    /// Нажатие на значок само снимает фокус с панели, и она прячется раньше, чем придёт клик, —
    /// поэтому клик сразу после такого скрытия считается закрытием, а не новым открытием.
    private void TogglePanel()
    {
        if (_panel is { } panel)
        {
            if (panel.IsVisible)
            {
                panel.HidePanel();
                return;
            }
            if (Environment.TickCount64 - panel.HiddenAt < 500)
                return;
        }
        ShowPanel();
    }

    private void Quit()
    {
        _showPanelWait?.Unregister(null);
        _showPanelEvent?.Dispose();
        Localization.Changed -= OnLanguageChanged;
        Theme.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _icons.Dispose();
        _watcher.Dispose();
        _updater.Dispose();
        _panel?.ClosePanel();
        _menuOwner?.Dispose();
        try
        {
            _node.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception e)
        {
            Log.Write($"Остановка: {e.Message}");
        }
        Log.Write("Clipvey завершён");
        Application.Current.Shutdown();
    }
}
