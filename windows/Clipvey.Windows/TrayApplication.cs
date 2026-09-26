using Clipvey.Core;

namespace Clipvey.Windows;

/// Приложение без главного окна: значок в трее, узел Clipvey и слежение за буфером.
internal sealed class TrayApplication : ApplicationContext
{
    private readonly ClipveyNode _node;
    private readonly ClipboardWatcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly SynchronizationContext _ui;
    private MainForm? _form;

    public TrayApplication()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var identity = Identity.LoadOrCreate(new DpapiSecretStore(AppPaths.DataDirectory));
        _node = new ClipveyNode(identity, new DeviceStore(AppPaths.DataDirectory), Environment.MachineName);
        _watcher = new ClipboardWatcher(OnLocalCopy);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _autostartItem = new ToolStripMenuItem("Запускать при входе в Windows") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        _autostartItem.CheckedChanged += (_, _) => Autostart.Set(_autostartItem.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть Clipvey", null, (_, _) => ShowForm());
        menu.Items.Add("Связать новое устройство", null, (_, _) =>
        {
            _node.StartPairingMode();
            ShowForm();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Открыть журнал", null, (_, _) => FileLog.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            Text = "Clipvey",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowForm();
        };

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
            _form?.ShowResult($"Связано с «{name}»");
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

    private void OnLocalCopy(string text)
    {
        if (_node.Devices.Any(device => device.Connected))
            _ = _node.BroadcastClipAsync(text);
    }

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

    private void Refresh()
    {
        var devices = _node.Devices;
        var status = devices.Count == 0
            ? "Нет связанных устройств"
            : $"Подключено {devices.Count(device => device.Connected)} из {devices.Count}";
        _statusItem.Text = $"Clipvey — {status}";
        _tray.Text = $"Clipvey: {status}";
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
