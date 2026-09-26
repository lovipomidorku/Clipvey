using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Окно Clipvey: связанные устройства (с выключателями) и связывание новых — в обеих ролях.
/// Закрытие окна только прячет его: программа продолжает работать в трее.
internal sealed class MainForm : Form
{
    private const int ContentWidth = 440;

    private readonly ClipveyNode _node;
    private readonly Label _summary = NewHeader("");
    private readonly Label _pairingHeader = NewHeader("");
    private readonly FlowLayoutPanel _devices = NewColumn();
    private readonly FlowLayoutPanel _pairing = NewColumn();
    private readonly Label _result = new()
    {
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 6, 0, 0),
    };
    private readonly Label _footer = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 12, 0, 0),
    };
    private readonly TextBox _codeBox = new() { MaxLength = 6, Width = 120, Font = new Font(FontFamily.GenericMonospace, 16) };

    // Исходящее связывание (роль I): этот компьютер вводит код с экрана другого устройства.
    private string? _outgoingPeer;
    private TaskCompletionSource<string?>? _codeRequest;
    private bool _codeAccepted;
    private CancellationTokenSource? _outgoingCancel;

    public MainForm(ClipveyNode node)
    {
        _node = node;
        Text = "Clipvey";
        Icon = AppIcon.Load(new Size(32, 32));
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var root = NewColumn();
        root.Controls.AddRange([_summary, _devices, _pairingHeader, _pairing, _result, _footer]);
        Controls.Add(root);

        _codeBox.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar);
        _codeBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                SubmitCode();
                e.SuppressKeyPress = true;
            }
        };
    }

    public void ShowResult(string text) => _result.Text = text;

    public void RefreshContent()
    {
        if (IsDisposed)
            return;
        SuspendLayout();
        RebuildDevices();
        RebuildPairing();
        _pairingHeader.Text = L("Связывание", "Pairing");
        _footer.Text = L($"Этот компьютер: {_node.Name} · порт {_node.Port}", $"This PC: {_node.Name} · port {_node.Port}");
        ResumeLayout(true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // MARK: - Устройства

    private void RebuildDevices()
    {
        var devices = _node.Devices;
        var connected = devices.Count(device => device.Connected);
        _summary.Text = devices.Count == 0
            ? L("Устройства", "Devices")
            : L($"Устройства: подключено {connected} из {devices.Count}", $"Devices: {connected} of {devices.Count} connected");
        Clear(_devices);
        if (devices.Count == 0)
        {
            _devices.Controls.Add(NewLabel(
                L("Свяжите этот компьютер с Mac или другим компьютером, и текст, скопированный на одном, можно будет вставить на другом.",
                  "Pair this PC with a Mac or another PC, and text you copy on one can be pasted on the other."),
                gray: true));
            return;
        }
        foreach (var device in devices)
            _devices.Controls.Add(DeviceRow(device));
    }

    private Control DeviceRow(DeviceStatus device)
    {
        var row = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, AutoSize = true, Width = ContentWidth, Margin = new Padding(0, 0, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var indicator = new Label
        {
            Text = "●",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = device.Connected ? Color.SeaGreen
                : device.Enabled && device.Problem is not null ? Color.DarkOrange
                : Color.Gray,
        };
        var status = !device.Enabled ? L("синхронизация выключена", "sync is off")
            : device.Connected ? L("подключено", "connected")
            : device.Problem ?? L("не в сети", "offline");
        var name = new Label { Text = $"{device.Name}\n{status}", AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(220, 0) };
        var toggle = new CheckBox { Text = L("Синхронизация", "Sync"), Checked = device.Enabled, AutoSize = true, Anchor = AnchorStyles.Left };
        toggle.CheckedChanged += (_, _) => _node.SetEnabled(device.DeviceId, toggle.Checked);
        var unpair = NewButton(L("Разорвать связь", "Unpair"), () =>
        {
            var answer = MessageBox.Show(this, L($"Разорвать связь с «{device.Name}»?", $"Unpair “{device.Name}”?"), "Clipvey",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
                _node.Unpair(device.DeviceId);
        });

        row.Controls.Add(indicator, 0, 0);
        row.Controls.Add(name, 1, 0);
        row.Controls.Add(toggle, 2, 0);
        row.Controls.Add(unpair, 3, 0);
        return row;
    }

    // MARK: - Связывание

    private void RebuildPairing()
    {
        _codeBox.Parent?.Controls.Remove(_codeBox);
        Clear(_pairing);

        if (_node.IncomingPairing is { } incoming)
        {
            // Роль R: этот компьютер показывает код.
            _pairing.Controls.Add(NewLabel(L($"Связывание с «{incoming.PeerName}»", $"Pairing with “{incoming.PeerName}”"), bold: true));
            _pairing.Controls.Add(NewLabel(
                incoming.Code is { } code ? $"{code[..3]} {code[3..]}" : "…",
                font: new Font(FontFamily.GenericMonospace, 26, FontStyle.Bold)));
            _pairing.Controls.Add(NewLabel(incoming.Verified
                ? L($"«{incoming.PeerName}» подтвердил код ✓ Нажмите «Готово».", $"“{incoming.PeerName}” confirmed the code ✓ Select Done.")
                : L($"Введите этот код на «{incoming.PeerName}».", $"Enter this code on “{incoming.PeerName}”.")));
            _pairing.Controls.Add(NewRow(
                NewButton(L("Готово", "Done"), incoming.Confirm, enabled: incoming.Verified),
                NewButton(L("Отмена", "Cancel"), incoming.Cancel)));
        }
        else if (_outgoingPeer is { } peer)
        {
            // Роль I: этот компьютер вводит код с экрана другого устройства.
            _pairing.Controls.Add(NewLabel(L($"Связывание с «{peer}»", $"Pairing with “{peer}”"), bold: true));
            if (_codeRequest is not null)
            {
                _pairing.Controls.Add(NewLabel(L($"Введите код с экрана «{peer}»:", $"Enter the code shown on “{peer}”:")));
                _pairing.Controls.Add(NewRow(_codeBox, NewButton(L("Подтвердить", "Confirm"), SubmitCode)));
            }
            else
            {
                _pairing.Controls.Add(NewLabel(_codeAccepted
                    ? L($"Код верный ✓ Нажмите «Готово» на «{peer}».", $"Code is correct ✓ Select Done on “{peer}”.")
                    : L("Подключение…", "Connecting…")));
            }
            _pairing.Controls.Add(NewRow(NewButton(L("Отмена", "Cancel"), CancelOutgoing)));
        }
        else if (_node.IsPairingMode)
        {
            _pairing.Controls.Add(NewLabel(L("Нажмите «Связать» и на другом устройстве. Затем выберите его здесь — или этот компьютер там.",
                "Select Pair on the other device too. Then choose it here, or choose this PC there.")));
            var candidates = _node.PairingCandidates;
            if (candidates.Count == 0)
                _pairing.Controls.Add(NewLabel(L("Поиск устройств…", "Looking for devices…"), gray: true));
            foreach (var candidate in candidates)
            {
                var label = NewLabel(candidate.Name);
                label.Anchor = AnchorStyles.Left;
                _pairing.Controls.Add(NewRow(label, NewButton(L("Связать", "Pair"), () => StartOutgoing(candidate))));
            }
            _pairing.Controls.Add(NewRow(NewButton(L("Закрыть", "Close"), _node.StopPairingMode)));
        }
        else
        {
            _pairing.Controls.Add(NewButton(L("Связать новое устройство", "Pair a new device"), () =>
            {
                _result.Text = "";
                _node.StartPairingMode();
            }));
        }
    }

    private async void StartOutgoing(DiscoveredDevice candidate)
    {
        _outgoingPeer = candidate.Name;
        _codeRequest = null;
        _codeAccepted = false;
        _result.Text = "";
        _outgoingCancel = new CancellationTokenSource();
        RefreshContent();
        try
        {
            await _node.PairWithAsync(candidate, RequestCodeAsync, () => OnUi(() =>
            {
                _codeAccepted = true;
                RefreshContent();
            }), _outgoingCancel.Token);
        }
        catch (Exception e)
        {
            // Причину пользователю показывает событие PairingFailed.
            Log.Write($"Связывание с «{candidate.Name}»: {e.Message}");
        }
        finally
        {
            _outgoingPeer = null;
            _codeRequest = null;
            _codeAccepted = false;
            _outgoingCancel?.Dispose();
            _outgoingCancel = null;
            RefreshContent();
        }
    }

    private Task<string?> RequestCodeAsync(string peerName, CancellationToken ct)
    {
        var request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => request.TrySetResult(null));
        OnUi(() =>
        {
            _codeRequest = request;
            _codeBox.Text = "";
            RefreshContent();
            _codeBox.Focus();
        });
        return request.Task;
    }

    private void SubmitCode()
    {
        if (_codeRequest is not { } request || _codeBox.Text.Length != 6)
            return;
        _codeRequest = null;
        request.TrySetResult(_codeBox.Text);
        RefreshContent();
    }

    private void CancelOutgoing()
    {
        if (_codeRequest is { } request)
        {
            _codeRequest = null;
            request.TrySetResult(null);
        }
        else
        {
            _outgoingCancel?.Cancel();
        }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    // MARK: - Элементы

    private static void Clear(Control parent)
    {
        foreach (var child in parent.Controls.Cast<Control>().ToList())
        {
            parent.Controls.Remove(child);
            child.Dispose();
        }
    }

    private Label NewLabel(string text, bool bold = false, bool gray = false, Font? font = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        Font = font ?? (bold ? new Font(Font, FontStyle.Bold) : Font),
        ForeColor = gray ? SystemColors.GrayText : SystemColors.ControlText,
        Margin = new Padding(0, 3, 0, 3),
    };

    private static Button NewButton(string text, Action onClick, bool enabled = true)
    {
        var button = new Button { Text = text, AutoSize = true, Enabled = enabled };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static FlowLayoutPanel NewRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 3, 0, 3),
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static FlowLayoutPanel NewColumn() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0),
    };

    private static Label NewHeader(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 4),
    };
}
