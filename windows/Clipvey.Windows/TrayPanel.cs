using System.Runtime.InteropServices;
using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Последняя синхронизация: когда и откуда (From — имя устройства; null — текст отправлен отсюда).
/// Только в памяти, без содержимого.
internal sealed record LastSync(DateTime Time, string? From);

/// Панель Clipvey у значка в трее: связанные устройства (с выключателями), связывание новых — в обеих ролях,
/// настройки. Без рамки, не видна в панели задач, прячется при потере фокуса (кроме как во время связывания).
/// Закрытие только прячет панель: программа продолжает работать в трее.
///
/// Содержимое целиком перестраивается в RefreshContent (смена данных, языка, темы, DPI).
/// Сама форма при этом не пересоздаётся: на ней держится состояние исходящего связывания.
/// Размеры — в пикселях при 96 DPI, пересчитываются через S() по DPI монитора панели.
internal sealed class TrayPanel : Form
{
    private const int ContentWidth = 344;

    private readonly ClipveyNode _node;
    private readonly FlowLayoutPanel _root;
    private readonly TextBox _codeBox = new() { MaxLength = 6, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center };
    private string _result = "";

    // Исходящее связывание (роль I): этот компьютер вводит код с экрана другого устройства.
    private string? _outgoingPeer;
    private TaskCompletionSource<string?>? _codeRequest;
    private bool _codeAccepted;
    private CancellationTokenSource? _outgoingCancel;

    /// Устройство, для которого показан вопрос «Разорвать связь?». Вопрос встроен в панель:
    /// MessageBox забрал бы фокус, и панель спряталась бы.
    private string? _confirmUnpair;

    /// Скругление углов через DWM сработало (Windows 11). Иначе рамку рисуем сами.
    private bool _roundedByDwm;
    private bool _loggedLook;

    /// Последняя синхронизация (null — ещё не было). Её помнит TrayApplication.
    private readonly Func<LastSync?> _lastSync;

    /// Когда панель спряталась из-за потери фокуса (Environment.TickCount64).
    public long HiddenAt { get; private set; }

    public TrayPanel(ClipveyNode node, Func<LastSync?> lastSync)
    {
        _node = node;
        _lastSync = lastSync;
        Text = "Clipvey";
        Icon = AppIcon.Load(new Size(32, 32));
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        DoubleBuffered = true;
        _dpi = DeviceDpi;

        _root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        Controls.Add(_root);

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

    private static Palette P => Theme.Current;

    /// DPI монитора, на котором стоит (или встанет) панель. По нему считаются размеры и шрифты.
    private int _dpi;

    private int S(int pixels) => pixels * _dpi / 96;

    private int Width96 => S(ContentWidth);

    public void ShowResult(string text)
    {
        _result = text;
        RefreshContent();
    }

    /// Связывание идёт — панель не прячется при потере фокуса, чтобы код оставался на экране.
    private bool IsPairingActive => _node.IncomingPairing is not null || _outgoingPeer is not null;

    /// Показать панель у области уведомлений и отдать ей фокус.
    public void ShowPanel()
    {
        // Сначала переносим окно на экран у курсора (там может быть другой DPI), потом строим содержимое.
        _dpi = PanelPlacement.CursorMonitorDpi() ?? DeviceDpi;
        Location = PanelPlacement.Locate(Size, _dpi);
        RefreshContent();
        PerformLayout();
        Location = PanelPlacement.Locate(Size, _dpi);
        var firstShow = !IsHandleCreated;
        Show();
        // Окно создаётся при первом показе, и WinForms может само подстроить размеры под DPI монитора.
        // После показа DPI окна точный — если что-то разошлось, перестраиваем ещё раз.
        if (firstShow || DeviceDpi != _dpi)
        {
            _dpi = DeviceDpi;
            RefreshContent();
            Location = PanelPlacement.Locate(Size, _dpi);
        }
        Activate();
        try
        {
            SetForegroundWindow(Handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        if (!_loggedLook)
        {
            _loggedLook = true;
            Log.Write($"Панель: DPI {_dpi} (окна {DeviceDpi}), тема {(P.IsDark ? "тёмная" : "светлая")}, шрифт {Theme.Text(14, _dpi).Name}");
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            const int CsDropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _roundedByDwm = Dwm.TryRoundCorners(Handle);
        Log.Write(_roundedByDwm ? "Панель: углы скруглены (DWM)" : "Панель: DWM не скругляет углы, рисуется рамка");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_roundedByDwm)
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, P.CardBorder, ButtonBorderStyle.Solid);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !IsPairingActive)
        {
            HiddenAt = Environment.TickCount64;
            Hide();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    /// Панель растёт и сжимается вместе с содержимым: угол у панели задач должен оставаться на месте.
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Visible)
            Location = PanelPlacement.Locate(Size, _dpi);
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _dpi = e.DeviceDpiNew;
        RefreshContent();
        if (Visible)
            Location = PanelPlacement.Locate(Size, _dpi);
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

    // MARK: - Содержимое

    public void RefreshContent()
    {
        if (IsDisposed)
            return;
        // Перестройка уничтожает элемент с фокусом — запоминаем его имя и возвращаем фокус.
        var focused = ContainsFocus ? FocusedControl()?.Name : null;

        SuspendLayout();
        _root.SuspendLayout();
        _codeBox.Parent?.Controls.Remove(_codeBox);
        Clear(_root);
        BackColor = P.Background;
        _root.BackColor = P.Background;
        Padding = new Padding(S(16), S(14), S(16), S(12));
        Font = Theme.Text(14, _dpi);

        _root.Controls.Add(NewLabel("Clipvey", Theme.Title(20, _dpi), P.Text, P.Background, Width96, new Padding(0, 0, 0, S(4))));
        AddDevices();
        AddPairing();
        if (_result.Length > 0)
            _root.Controls.Add(NewLabel(_result, Theme.Text(13, _dpi), P.SecondaryText, P.Background, Width96, new Padding(S(2), S(2), 0, S(4))));
        AddSettings();
        _root.Controls.Add(NewLabel(
            L($"Этот компьютер: {_node.Name} · порт {_node.Port}", $"This PC: {_node.Name} · port {_node.Port}"),
            Theme.Text(12, _dpi), P.SecondaryText, P.Background, Width96, new Padding(S(2), S(8), 0, 0)));

        _root.ResumeLayout(true);
        ResumeLayout(true);
        Invalidate(true);

        if (focused is { Length: > 0 } && _root.Controls.Find(focused, searchAllChildren: true).FirstOrDefault() is { } restored)
            restored.Focus();
    }

    private Control? FocusedControl()
    {
        Control? control = this;
        while (control is ContainerControl { ActiveControl: { } active })
            control = active;
        // ActiveControl у формы может указывать на панель-контейнер: спускаемся до элемента с фокусом.
        while (control is not null && !control.Focused && control.Controls.Cast<Control>().FirstOrDefault(c => c.ContainsFocus) is { } inner)
            control = inner;
        return control;
    }

    private void AddSection(string title, string? note = null)
    {
        var header = NewTable(Width96, P.Background, new Padding(S(2), S(10), 0, S(6)));
        header.Controls.Add(NewLabel(title, Theme.Title(14, _dpi), P.Text, P.Background, Width96, new Padding(0)), 0, 0);
        if (note is not null)
        {
            var right = NewLabel(note, Theme.Text(12, _dpi), P.SecondaryText, P.Background, Width96 / 2, new Padding(0));
            right.Anchor = AnchorStyles.Right;
            header.Controls.Add(right, 2, 0);
        }
        _root.Controls.Add(header);
    }

    // MARK: - Устройства

    private void AddDevices()
    {
        var devices = _node.Devices;
        var connected = devices.Count(device => device.Connected);
        AddSection(L("Устройства", "Devices"), devices.Count == 0
            ? null
            : L($"подключено {connected} из {devices.Count}", $"{connected} of {devices.Count} connected"));

        if (devices.Count == 0)
        {
            var card = NewCard();
            card.Controls.Add(NewLabel(
                L("Свяжите этот компьютер с Mac или другим компьютером, и текст, скопированный на одном, можно будет вставить на другом.",
                  "Pair this PC with a Mac or another PC, and text you copy on one can be pasted on the other."),
                Theme.Text(14, _dpi), P.SecondaryText, P.Card, CardInnerWidth, new Padding(0)));
            _root.Controls.Add(card);
            return;
        }

        // Много устройств — список прокручивается, чтобы панель не выросла выше экрана.
        Control target = _root;
        if (devices.Count > 4)
        {
            var scroll = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = P.Background,
                Margin = new Padding(0),
                Size = new Size(Width96 + SystemInformation.VerticalScrollBarWidth, S(4 * 82)),
            };
            scroll.HandleCreated += (_, _) => Theme.ApplyScrollbarTheme(scroll);
            _root.Controls.Add(scroll);
            target = scroll;
        }
        foreach (var device in devices)
            target.Controls.Add(DeviceCard(device));
        _root.Controls.Add(NewLabel(LastSyncText(), Theme.Text(12, _dpi), P.SecondaryText, P.Background, Width96,
            new Padding(S(2), 0, 0, S(2))));
    }

    /// «Последняя синхронизация: 11:03 · от OFFICE-PC» / «… 11:05 · отправлено».
    private string LastSyncText()
    {
        if (_lastSync() is not { } sync)
            return L("Синхронизаций пока не было", "No syncs yet");
        var time = sync.Time.Date == DateTime.Today ? sync.Time.ToString("t") : sync.Time.ToString("g");
        return sync.From is { } from
            ? L($"Последняя синхронизация: {time} · от {from}", $"Last sync: {time} · from {from}")
            : L($"Последняя синхронизация: {time} · отправлено", $"Last sync: {time} · sent");
    }

    private int CardInnerWidth => Width96 - S(28);

    private Control DeviceCard(DeviceStatus device)
    {
        var card = NewCard();
        var table = NewTable(CardInnerWidth, P.Card, new Padding(0));

        var dotColor = device.Connected ? P.Success
            : device.Enabled && device.Problem is not null ? P.Warning
            : P.Disabled;
        var dot = NewLabel("●", Theme.Text(12, _dpi), dotColor, P.Card, S(20), new Padding(0, S(3), S(6), 0));
        dot.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        dot.AccessibleName = "";
        dot.AccessibleRole = AccessibleRole.None;

        var status = !device.Enabled ? L("синхронизация выключена", "sync is off")
            : device.Connected ? L("подключено", "connected")
            : device.Problem ?? L("не в сети", "offline");
        var textWidth = CardInnerWidth - S(20 + 6 + 52);
        var texts = NewColumn(P.Card);
        texts.Controls.Add(NewLabel(device.Name, Theme.Text(14, _dpi, FontStyle.Bold), P.Text, P.Card, textWidth, new Padding(0)));
        texts.Controls.Add(NewLabel(status, Theme.Text(12, _dpi),
            device.Enabled && !device.Connected && device.Problem is not null ? P.Warning : P.SecondaryText,
            P.Card, textWidth, new Padding(0, S(2), 0, 0)));
        texts.Anchor = AnchorStyles.Left;

        var toggle = new ToggleSwitch(P, _dpi, P.Card, L($"Синхронизация с «{device.Name}»", $"Sync with “{device.Name}”"))
        {
            Checked = device.Enabled,
            Name = "toggle:" + device.DeviceId,
            Anchor = AnchorStyles.Right,
        };
        toggle.Toggled += (_, _) => _node.SetEnabled(device.DeviceId, toggle.Checked);

        table.Controls.Add(dot, 0, 0);
        table.Controls.Add(texts, 1, 0);
        table.Controls.Add(toggle, 2, 0);

        Control actions;
        if (_confirmUnpair == device.DeviceId)
        {
            var question = NewColumn(P.Card);
            question.Controls.Add(NewLabel(L($"Разорвать связь с «{device.Name}»? Чтобы снова синхронизироваться, их придётся связать заново.",
                    $"Unpair “{device.Name}”? You’ll need to pair again to sync."),
                Theme.Text(13, _dpi), P.Text, P.Card, textWidth + S(52), new Padding(0, S(8), 0, S(6))));
            question.Controls.Add(NewRow(P.Card,
                NewButton(L("Разорвать связь", "Unpair"), () =>
                {
                    _confirmUnpair = null;
                    _node.Unpair(device.DeviceId);
                }, ButtonKind.Danger, P.Card, "unpair-yes:" + device.DeviceId),
                NewButton(L("Отмена", "Cancel"), () =>
                {
                    _confirmUnpair = null;
                    RefreshContent();
                }, ButtonKind.Standard, P.Card, "unpair-no:" + device.DeviceId)));
            actions = question;
        }
        else
        {
            actions = NewButton(L("Разорвать связь", "Unpair"), () =>
            {
                _confirmUnpair = device.DeviceId;
                RefreshContent();
            }, ButtonKind.Subtle, P.Card, "unpair:" + device.DeviceId);
            actions.Margin = new Padding(0, S(4), 0, 0);
        }
        table.Controls.Add(actions, 1, 1);
        table.SetColumnSpan(actions, 2);

        card.Controls.Add(table);
        return card;
    }

    // MARK: - Связывание

    private void AddPairing()
    {
        AddSection(L("Связывание", "Pairing"));
        var card = NewCard();
        var column = NewColumn(P.Card);
        card.Controls.Add(column);
        _root.Controls.Add(card);

        var width = CardInnerWidth;
        if (_node.IncomingPairing is { } incoming)
        {
            // Роль R: этот компьютер показывает код.
            column.Controls.Add(Strong(L($"Связывание с «{incoming.PeerName}»", $"Pairing with “{incoming.PeerName}”")));
            column.Controls.Add(NewLabel(
                incoming.Code is { } code ? $"{code[..3]} {code[3..]}" : "…",
                Theme.Mono(34, _dpi, FontStyle.Bold), P.Text, P.Card, width, new Padding(0, S(6), 0, S(6))));
            column.Controls.Add(Plain(incoming.Verified
                ? L($"«{incoming.PeerName}» подтвердил код ✓ Нажмите «Готово».", $"“{incoming.PeerName}” confirmed the code ✓ Select Done.")
                : L($"Введите этот код на «{incoming.PeerName}».", $"Enter this code on “{incoming.PeerName}”.")));
            column.Controls.Add(NewRow(P.Card,
                NewButton(L("Готово", "Done"), incoming.Confirm, ButtonKind.Accent, P.Card, "pair-done", enabled: incoming.Verified),
                NewButton(L("Отмена", "Cancel"), incoming.Cancel, ButtonKind.Standard, P.Card, "pair-cancel")));
        }
        else if (_outgoingPeer is { } peer)
        {
            // Роль I: этот компьютер вводит код с экрана другого устройства.
            column.Controls.Add(Strong(L($"Связывание с «{peer}»", $"Pairing with “{peer}”")));
            if (_codeRequest is not null)
            {
                column.Controls.Add(Plain(L($"Введите код с экрана «{peer}»:", $"Enter the code shown on “{peer}”:")));
                _codeBox.Font = Theme.Mono(22, _dpi);
                _codeBox.BackColor = P.Input;
                _codeBox.ForeColor = P.Text;
                _codeBox.Width = S(140);
                _codeBox.Margin = new Padding(0, 0, S(8), 0);
                _codeBox.AccessibleName = L("Код связывания", "Pairing code");
                var submit = NewButton(L("Подтвердить", "Confirm"), SubmitCode, ButtonKind.Accent, P.Card, "code-submit");
                submit.Anchor = AnchorStyles.Left;
                column.Controls.Add(NewRow(P.Card, _codeBox, submit));
            }
            else
            {
                column.Controls.Add(Plain(_codeAccepted
                    ? L($"Код верный ✓ Нажмите «Готово» на «{peer}».", $"Code is correct ✓ Select Done on “{peer}”.")
                    : L("Подключение…", "Connecting…")));
            }
            column.Controls.Add(NewRow(P.Card, NewButton(L("Отмена", "Cancel"), CancelOutgoing, ButtonKind.Standard, P.Card, "outgoing-cancel")));
        }
        else if (_node.IsPairingMode)
        {
            column.Controls.Add(Plain(L("Нажмите «Связать» и на другом устройстве. Затем выберите его здесь — или этот компьютер там.",
                "Select Pair on the other device too. Then choose it here, or choose this PC there.")));
            var candidates = _node.PairingCandidates;
            if (candidates.Count == 0)
                column.Controls.Add(NewLabel(L("Поиск устройств…", "Looking for devices…"), Theme.Text(13, _dpi), P.SecondaryText, P.Card, width, new Padding(0, S(6), 0, S(6))));
            foreach (var candidate in candidates)
            {
                var row = NewTable(width, P.Card, new Padding(0, S(4), 0, S(4)));
                var name = NewLabel(candidate.Name, Theme.Text(14, _dpi, FontStyle.Bold), P.Text, P.Card, width - S(110), new Padding(0));
                name.Anchor = AnchorStyles.Left;
                var pair = NewButton(L("Связать", "Pair"), () => StartOutgoing(candidate), ButtonKind.Accent, P.Card,
                    "candidate:" + (candidate.DeviceId ?? candidate.Name));
                pair.Anchor = AnchorStyles.Right;
                pair.Margin = new Padding(0);
                row.Controls.Add(name, 0, 0);
                row.Controls.Add(pair, 2, 0);
                column.Controls.Add(row);
            }
            column.Controls.Add(NewRow(P.Card, NewButton(L("Закрыть", "Close"), _node.StopPairingMode, ButtonKind.Standard, P.Card, "pairing-close")));
        }
        else
        {
            column.Controls.Add(NewButton(L("Связать новое устройство", "Pair a new device"), () =>
            {
                _result = "";
                _node.StartPairingMode();
            }, ButtonKind.Accent, P.Card, "pairing-start"));
        }
    }

    private Label Strong(string text) =>
        NewLabel(text, Theme.Text(14, _dpi, FontStyle.Bold), P.Text, P.Card, CardInnerWidth, new Padding(0, 0, 0, S(4)));

    private Label Plain(string text) =>
        NewLabel(text, Theme.Text(14, _dpi), P.Text, P.Card, CardInnerWidth, new Padding(0, S(2), 0, S(8)));

    private async void StartOutgoing(DiscoveredDevice candidate)
    {
        _outgoingPeer = candidate.Name;
        _codeRequest = null;
        _codeAccepted = false;
        _result = "";
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

    // MARK: - Настройки

    private void AddSettings()
    {
        AddSection(L("Настройки", "Settings"));
        var card = NewCard();
        var table = NewTable(CardInnerWidth, P.Card, new Padding(0));
        var labelWidth = CardInnerWidth - S(150);

        var autostartText = L("Запускать при входе в Windows", "Start with Windows");
        var autostartLabel = NewLabel(autostartText, Theme.Text(14, _dpi), P.Text, P.Card, labelWidth, new Padding(0));
        autostartLabel.Anchor = AnchorStyles.Left;
        var autostart = new ToggleSwitch(P, _dpi, P.Card, autostartText)
        {
            Checked = SafeAutostartIsEnabled(),
            Name = "autostart",
            Anchor = AnchorStyles.Right,
        };
        autostart.Toggled += (_, _) =>
        {
            try
            {
                Autostart.Set(autostart.Checked);
            }
            catch (Exception e)
            {
                Log.Write($"Не удалось изменить автозапуск: {e.Message}");
                autostart.Checked = SafeAutostartIsEnabled();
            }
        };
        table.Controls.Add(autostartLabel, 0, 0);
        table.Controls.Add(autostart, 2, 0);

        var languageLabel = NewLabel(L("Язык", "Language"), Theme.Text(14, _dpi), P.Text, P.Card, labelWidth, new Padding(0, S(10), 0, 0));
        languageLabel.Anchor = AnchorStyles.Left;
        var current = TrayApplication.LanguageChoices().First(choice => choice.Value == Setting).Title;
        var language = NewButton(current + "  ▾", () => { }, ButtonKind.Standard, P.Card, "language");
        language.AccessibleName = L($"Язык: {current}", $"Language: {current}");
        language.Anchor = AnchorStyles.Right;
        language.Margin = new Padding(0, S(10), 0, 0);
        language.Click += (_, _) => ShowLanguageMenu(language);
        table.Controls.Add(languageLabel, 0, 1);
        table.Controls.Add(language, 2, 1);

        card.Controls.Add(table);
        _root.Controls.Add(card);
    }

    private static bool SafeAutostartIsEnabled()
    {
        try
        {
            return Autostart.IsEnabled;
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось прочитать автозапуск: {e.Message}");
            return false;
        }
    }

    /// Выбор языка — выпадающим меню: оно не забирает фокус, и панель не прячется.
    private void ShowLanguageMenu(Control anchor)
    {
        var menu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(P) };
        foreach (var (value, title) in TrayApplication.LanguageChoices())
        {
            var item = new ToolStripMenuItem(title) { Checked = Setting == value };
            // Смену откладываем: она перестраивает панель, а меню ещё обрабатывает клик.
            item.Click += (_, _) => BeginInvoke(() => Localization.Set(value));
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    // MARK: - Элементы

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    private static void Clear(Control parent)
    {
        foreach (var child in parent.Controls.Cast<Control>().ToList())
        {
            parent.Controls.Remove(child);
            child.Dispose();
        }
    }

    private Card NewCard()
    {
        var card = new Card(P, _dpi)
        {
            MinimumSize = new Size(Width96, 0),
            MaximumSize = new Size(Width96, 0),
            Margin = new Padding(0, 0, 0, S(8)),
        };
        return card;
    }

    private static Label NewLabel(string text, Font font, Color color, Color back, int maxWidth, Padding margin) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        Font = font,
        ForeColor = color,
        BackColor = back,
        UseMnemonic = false,
        Margin = margin,
    };

    private ThemedButton NewButton(string text, Action onClick, ButtonKind kind, Color back, string name, bool enabled = true)
    {
        var button = new ThemedButton(text, P, kind, _dpi, back) { Enabled = enabled, Name = name };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// Строка: слева растягиваемая колонка 1, справа колонка 2 (колонка 0 — для значков).
    private static TableLayoutPanel NewTable(int width, Color back, Padding margin)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 0),
            MaximumSize = new Size(width, 0),
            BackColor = back,
            Margin = margin,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return table;
    }

    private FlowLayoutPanel NewRow(Color back, params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximumSize = new Size(CardInnerWidth, 0),
            BackColor = back,
            Margin = new Padding(0, S(2), 0, 0),
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static FlowLayoutPanel NewColumn(Color back) => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = back,
        Margin = new Padding(0),
    };

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
