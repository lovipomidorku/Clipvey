using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Приложение без главного окна: значок в трее, узел Clipvey и слежение за буфером.
internal sealed class TrayApplication : ApplicationContext
{
    /// Предел длины подсказки у значка в трее (NotifyIcon.Text бросает исключение на длинной строке).
    private const int MaxTrayText = 63;

    private readonly ClipveyNode _node;
    private readonly ClipboardWatcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly TrayIcons _icons = new();
    private TrayState? _trayState;
    private readonly SynchronizationContext _ui;
    private ToolStripMenuItem? _statusItem;
    private TrayPanel? _form;
    private LastSync? _lastSync;
    private readonly Updater _updater;

    public TrayApplication()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var identity = Identity.LoadOrCreate(new DpapiSecretStore(AppPaths.DataDirectory));
        _node = new ClipveyNode(identity, new DeviceStore(AppPaths.DataDirectory), Environment.MachineName);
        _watcher = new ClipboardWatcher(OnLocalCopy);

        _tray = new NotifyIcon
        {
            Icon = _icons.Get(TrayState.Idle),
            Text = "Clipvey",
            Visible = true,
        };
        BuildMenu();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                TogglePanel();
        };
        Localization.Changed += OnLanguageChanged;
        Theme.Start(_ui);
        Theme.Changed += OnThemeChanged;

        _node.ClipReceived += (text, from) => OnUi(() =>
        {
            _watcher.WriteRemote(text);
            NoteSync(new LastSync(DateTime.Now, from));
        });
        _node.Changed += () => OnUi(Refresh);
        _node.IncomingPairingChanged += incoming => OnUi(() =>
        {
            if (incoming is not null)
                ShowForm();
            Refresh();
        });
        _node.PairingSucceeded += name => OnUi(() =>
        {
            _form?.ShowResult(L($"Связано с «{name}»", $"Paired with “{name}”"));
            Refresh();
        });
        _node.PairingFailed += reason => OnUi(() =>
        {
            _form?.ShowResult(reason);
            Refresh();
        });

        _node.Start();
        Refresh();

        _updater = new Updater(() => OnUi(Quit));
        _updater.Changed += () => OnUi(() =>
        {
            if (_form is { Visible: true })
                _form.RefreshContent();
        });
        _updater.UpdateFound += version => OnUi(() => _tray.ShowBalloonTip(15000, "Clipvey",
            L($"Доступна версия {version} — обновить?", $"Version {version} is available. Update?"), ToolTipIcon.Info));
        _tray.BalloonTipClicked += (_, _) => OnUi(ShowForm);
        _updater.Start();
    }

    /// Меню по правой кнопке. Пересобирается целиком при смене языка.
    private void BuildMenu()
    {
        var old = _tray.ContextMenuStrip;
        _statusItem = new ToolStripMenuItem { Enabled = false };
        var autostart = new ToolStripMenuItem(L("Запускать при входе в Windows", "Start with Windows"))
        {
            Checked = Autostart.IsEnabled,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);

        var language = new ToolStripMenuItem(L("Язык", "Language"));
        foreach (var (value, title) in LanguageChoices())
        {
            var item = new ToolStripMenuItem(title) { Checked = Setting == value };
            // Смена языка пересобирает это меню — откладываем её, пока меню обрабатывает клик.
            item.Click += (_, _) => OnUi(() => Localization.Set(value));
            language.DropDownItems.Add(item);
        }

        var menu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(Theme.Current) };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        // Панель открываем после закрытия меню: иначе меню, закрываясь, вернёт фокус значку и панель сразу спрячется.
        menu.Items.Add(L("Открыть Clipvey", "Open Clipvey"), null, (_, _) => OnUi(ShowForm));
        menu.Items.Add(L("Связать новое устройство", "Pair a new device"), null, (_, _) => OnUi(() =>
        {
            _node.StartPairingMode();
            ShowForm();
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(language);
        menu.Items.Add(L("Открыть журнал", "Open log"), null, (_, _) => FileLog.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L("Выход", "Quit"), null, (_, _) => Quit());
        // Автозапуск могли поменять в панели — отметка в меню обновляется при каждом открытии.
        menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;

        _tray.ContextMenuStrip = menu;
        if (old is not null)
            OnUi(old.Dispose);
    }

    /// Варианты настройки «Язык». Названия языков пишутся на самих языках.
    public static IEnumerable<(AppLanguage Value, string Title)> LanguageChoices() =>
    [
        (AppLanguage.System, L("Как в системе", "Use system setting")),
        (AppLanguage.Russian, "Русский"),
        (AppLanguage.English, "English"),
    ];

    private void OnLanguageChanged()
    {
        BuildMenu();
        Refresh();
    }

    private void OnThemeChanged()
    {
        BuildMenu();
        _form?.RefreshContent();
    }

    private void OnLocalCopy(string text)
    {
        if (!_node.Devices.Any(device => device.Connected))
            return;
        _ = _node.BroadcastClipAsync(text);
        // «Отправлено» — приблизительно: ошибки отправки узел пишет в журнал, но наружу не сообщает.
        // Слишком длинный текст узел не отправляет — его не отмечаем.
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= Protocol.MaxClipBytes)
            NoteSync(new LastSync(DateTime.Now, From: null));
    }

    private void NoteSync(LastSync sync)
    {
        _lastSync = sync;
        if (_form is { Visible: true })
            _form.RefreshContent();
    }

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

    private void Refresh()
    {
        var devices = _node.Devices;
        var connected = devices.Count(device => device.Connected);
        var status = devices.Count == 0
            ? L("Нет связанных устройств", "No paired devices")
            : L($"Подключено {connected} из {devices.Count}", $"{connected} of {devices.Count} connected");
        if (_statusItem is not null)
            _statusItem.Text = $"Clipvey — {status}";
        if (devices.Count > 0 && devices.All(device => !device.Enabled))
            status = L("Синхронизация выключена", "Sync is off");
        UpdateTrayIcon(connected > 0 ? TrayState.Connected
            : devices.Count > 0 && devices.All(device => !device.Enabled) ? TrayState.AllDisabled
            : TrayState.Idle);
        var tip = $"Clipvey: {status}";
        _tray.Text = tip.Length <= MaxTrayText ? tip : tip[..(MaxTrayText - 1)] + "…";
        // Скрытая панель перестроится при показе.
        if (_form is { Visible: true })
            _form.RefreshContent();
    }

    private void UpdateTrayIcon(TrayState state)
    {
        var icon = _icons.Get(state);
        if (_trayState == state && ReferenceEquals(_tray.Icon, icon))
            return;
        if (_trayState != state)
            Log.Write($"Значок трея: {state}");
        _trayState = state;
        _tray.Icon = icon;
        _icons.ReleaseStale();
    }

    private void ShowForm()
    {
        if (_form is null || _form.IsDisposed)
            _form = new TrayPanel(_node, () => _lastSync, _updater);
        _form.ShowPanel();
    }

    /// Левый клик по значку: открыть панель или закрыть открытую.
    /// Нажатие на значок само снимает фокус с панели, и она прячется раньше, чем придёт клик, —
    /// поэтому клик сразу после такого скрытия считается закрытием, а не новым открытием.
    private void TogglePanel()
    {
        if (_form is { IsDisposed: false } form)
        {
            if (form.Visible)
            {
                form.Hide();
                return;
            }
            if (Environment.TickCount64 - form.HiddenAt < 500)
                return;
        }
        ShowForm();
    }

    private void Quit()
    {
        Localization.Changed -= OnLanguageChanged;
        Theme.Changed -= OnThemeChanged;
        Theme.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _icons.Dispose();
        _watcher.Dispose();
        _updater.Dispose();
        _form?.Dispose();
        try
        {
            _node.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception e)
        {
            Log.Write($"Остановка: {e.Message}");
        }
        Log.Write("Clipvey завершён");
        ExitThread();
    }
}
