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
    private readonly SynchronizationContext _ui;
    private ToolStripMenuItem? _statusItem;
    private MainForm? _form;

    public TrayApplication()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var identity = Identity.LoadOrCreate(new DpapiSecretStore(AppPaths.DataDirectory));
        _node = new ClipveyNode(identity, new DeviceStore(AppPaths.DataDirectory), Environment.MachineName);
        _watcher = new ClipboardWatcher(OnLocalCopy);

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            Text = "Clipvey",
            Visible = true,
        };
        BuildMenu();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowForm();
        };
        Localization.Changed += OnLanguageChanged;

        _node.ClipReceived += (text, _) => OnUi(() => _watcher.WriteRemote(text));
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
            item.Click += (_, _) => Localization.Set(value);
            language.DropDownItems.Add(item);
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L("Открыть Clipvey", "Open Clipvey"), null, (_, _) => ShowForm());
        menu.Items.Add(L("Связать новое устройство", "Pair a new device"), null, (_, _) =>
        {
            _node.StartPairingMode();
            ShowForm();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(language);
        menu.Items.Add(L("Открыть журнал", "Open log"), null, (_, _) => FileLog.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L("Выход", "Quit"), null, (_, _) => Quit());
        // Автозапуск могли поменять в панели — отметка в меню обновляется при каждом открытии.
        menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;

        _tray.ContextMenuStrip = menu;
        old?.Dispose();
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

    private void OnLocalCopy(string text)
    {
        if (_node.Devices.Any(device => device.Connected))
            _ = _node.BroadcastClipAsync(text);
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
        var tip = $"Clipvey: {status}";
        _tray.Text = tip.Length <= MaxTrayText ? tip : tip[..(MaxTrayText - 1)] + "…";
        _form?.RefreshContent();
    }

    private void ShowForm()
    {
        if (_form is null || _form.IsDisposed)
            _form = new MainForm(_node);
        _form.RefreshContent();
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void Quit()
    {
        Localization.Changed -= OnLanguageChanged;
        _tray.Visible = false;
        _watcher.Dispose();
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
