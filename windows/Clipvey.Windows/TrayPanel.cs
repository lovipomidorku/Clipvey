using System.Runtime.InteropServices;
using Clipvey.Core;
using static Clipvey.Windows.Localization;

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

/// Панель Clipvey у значка в трее: связанные устройства (с выключателями), связывание новых — в обеих ролях,
/// настройки. Без рамки, не видна в панели задач, прячется при потере фокуса (кроме как во время связывания).
/// Закрытие только прячет панель: программа продолжает работать в трее.
///
/// Содержимое целиком перестраивается в RefreshContent (смена данных, языка, темы, DPI).
/// Сама форма при этом не пересоздаётся: на ней держится состояние исходящего связывания.
/// Размеры — в пикселях при 96 DPI, пересчитываются через S() по DPI монитора панели.
internal sealed partial class TrayPanel : Form
{
    private const int ContentWidth = 344;

    private readonly ClipveyNode _node;
    private readonly FlowLayoutPanel _root;
    private readonly TextBox _codeBox = new() { Name = "code", MaxLength = 6, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center };
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

    /// Обновления: полоса «Доступна версия» и пункты настроек (UpdaterPanel.cs).
    private readonly Updater _updater;

    /// Сменить своё имя и «Передавать картинки» (сохраняет TrayApplication).
    private readonly Action<string> _rename;
    private readonly Action<bool> _setImagesEnabled;

    /// Поле «Имя этого компьютера». Живёт вместе с панелью (как поле кода): перестройка содержимого
    /// только отцепляет его, поэтому набранный текст и курсор сохраняются.
    private readonly TextBox _nameBox = new() { Name = "self-name", MaxLength = 63, BorderStyle = BorderStyle.FixedSingle };

    /// Имя в поле изменено пользователем и ещё не сохранено.
    private bool _nameDirty;
    private bool _settingName;

    /// Поле псевдонима и устройство, которое сейчас переименовывается (null — никакое).
    private readonly TextBox _aliasBox = new() { Name = "alias", MaxLength = 63, BorderStyle = BorderStyle.FixedSingle };
    private string? _renaming;

    /// Идёт перестройка содержимого: потеря фокуса полем имени в это время — не повод сохранять.
    private bool _rebuilding;

    /// Когда панель спряталась из-за потери фокуса (Environment.TickCount64).
    public long HiddenAt { get; private set; }

    public TrayPanel(ClipveyNode node, Func<LastSync?> lastSync, Updater updater, Action<string> rename, Action<bool> setImagesEnabled)
    {
        _node = node;
        _lastSync = lastSync;
        _updater = updater;
        _rename = rename;
        _setImagesEnabled = setImagesEnabled;
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

        _nameBox.TextChanged += (_, _) =>
        {
            if (!_settingName)
                _nameDirty = true;
        };
        _nameBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitName();
                e.SuppressKeyPress = true;
            }
        };
        _nameBox.Leave += (_, _) =>
        {
            if (!_rebuilding)
                CommitName();
        };
        _aliasBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitAlias();
                e.SuppressKeyPress = true;
            }
        };
    }

    private static Palette P => Theme.Current;

    /// Экран, на котором открыта панель. При изменении размера панель остаётся на нём,
    /// даже если курсор уже на другом мониторе.
    private Screen? _screen;

    /// DPI монитора, на котором стоит (или встанет) панель. По нему считаются размеры и шрифты.
    private int _dpi;

    private Point Locate() => PanelPlacement.Locate(Size, _dpi, _screen ?? Screen.FromPoint(Cursor.Position));

    private int S(int pixels) => pixels * _dpi / 96;

    private int Width96 => S(ContentWidth);

    /// Причина неудачи на языке интерфейса (узел отдаёт только код).
    public static string FailureText(FailureReason reason) => reason switch
    {
        FailureReason.NotPairing => L("На другом устройстве не открыт режим связывания", "Pairing isn’t open on the other device"),
        FailureReason.Busy => L("Другое устройство уже связывается с кем-то", "The other device is already pairing with someone"),
        FailureReason.PeerCodeMismatch => L("На другом устройстве введён неверный код", "A wrong code was entered on the other device"),
        FailureReason.PeerCancelled => L("Связывание отменено на другом устройстве", "Pairing was cancelled on the other device"),
        FailureReason.CommitMismatch => L("Проверка связывания не прошла — возможно, соединение перехвачено", "Pairing check failed — the connection may be intercepted"),
        FailureReason.UnknownDevice => L("Другое устройство не знает этот компьютер — свяжите заново", "The other device doesn’t know this PC — pair again"),
        FailureReason.Disabled => L("На другом устройстве синхронизация с этим компьютером выключена", "The other device has sync with this PC turned off"),
        FailureReason.Rejected => L("Другое устройство отказало", "The other device refused"),
        FailureReason.CodeMismatch => L("Код не совпал", "The code doesn’t match"),
        FailureReason.Cancelled => L("Связывание отменено или истекло время", "Pairing was cancelled or timed out"),
        FailureReason.WrongDevice => L("По адресу ответило другое устройство", "A different device answered at this address"),
        FailureReason.ConnectionFailed => L("Нет соединения", "Couldn’t connect"),
        _ => L("Ошибка обмена с другим устройством", "Communication error with the other device"),
    };

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
        _screen = Screen.FromPoint(Cursor.Position);
        _dpi = PanelPlacement.CursorMonitorDpi() ?? DeviceDpi;
        Location = Locate();
        RefreshContent();
        PerformLayout();
        Location = Locate();
        var firstShow = !IsHandleCreated;
        Show();
        // Окно создаётся при первом показе, и WinForms может само подстроить размеры под DPI монитора.
        // После показа DPI окна точный — если что-то разошлось, перестраиваем ещё раз.
        if (firstShow || DeviceDpi != _dpi)
        {
            _dpi = DeviceDpi;
            RefreshContent();
            Location = Locate();
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

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
            CommitName();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !IsPairingActive)
            BeginInvoke(HideIfFocusLeftApp);
    }

    /// Прятать панель, только если фокус ушёл в другую программу. Меню «⋯» и выбор языка — отдельные окна,
    /// а перестройка панели уничтожает элемент с фокусом: это тоже снимает активность, но фокус остаётся в Clipvey.
    private async void HideIfFocusLeftApp()
    {
        await Task.Delay(120);
        if (!Visible || IsPairingActive || ActiveForm == this)
            return;
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        if (foreground == IntPtr.Zero || process == (uint)Environment.ProcessId)
        {
            Log.Write($"Панель: активность ушла в окно Clipvey или никуда ({WindowClass(foreground)}) — не прячем");
            return;
        }
        HiddenAt = Environment.TickCount64;
        Hide();
    }

    /// После закрытия меню вернуть активность панели, чтобы следующий клик мимо её спрятал.
    private void ReactivateAfterMenu() => BeginInvoke(() =>
    {
        if (Visible && !IsDisposed)
            Activate();
    });

    private static string WindowClass(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return "нет окна";
        var name = new System.Text.StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "?";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape)
            return;
        // Esc во время правки отменяет правку, а не прячет панель.
        if (_renaming is not null)
        {
            _renaming = null;
            RefreshContent();
        }
        else if (_nameBox.Focused && _nameDirty)
        {
            _nameDirty = false;
            SetNameText(_node.Name);
            _nameBox.SelectAll();
        }
        else
        {
            Hide();
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// Панель растёт и сжимается вместе с содержимым: угол у панели задач должен оставаться на месте.
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Visible)
            Location = Locate();
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _dpi = e.DeviceDpiNew;
        RefreshContent();
        if (Visible)
            Location = Locate();
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

        _rebuilding = true;
        SuspendLayout();
        _root.SuspendLayout();
        _codeBox.Parent?.Controls.Remove(_codeBox);
        _nameBox.Parent?.Controls.Remove(_nameBox);
        _aliasBox.Parent?.Controls.Remove(_aliasBox);
        Clear(_root);
        BackColor = P.Background;
        _root.BackColor = P.Background;
        Padding = new Padding(S(16), S(14), S(16), S(12));
        Font = Theme.Text(14, _dpi);

        _root.Controls.Add(NewLabel("Clipvey", Theme.Title(16, _dpi), P.Text, P.Background, Width96, new Padding(0, 0, 0, S(4))));
        AddUpdateBanner();
        AddDevices();
        AddPairing();
        if (_result.Length > 0)
            _root.Controls.Add(NewLabel(_result, Theme.Text(13, _dpi), P.SecondaryText, P.Background, Width96, new Padding(S(2), S(2), 0, S(4))));
        AddSettings();
        AddUpdateSettings();
        _root.Controls.Add(NewLabel(
            L($"Этот компьютер: {_node.Name} · порт {_node.Port}", $"This PC: {_node.Name} · port {_node.Port}"),
            Theme.Text(12, _dpi), P.SecondaryText, P.Background, Width96, new Padding(S(2), S(8), 0, 0)));

        _root.ResumeLayout(true);
        ResumeLayout(true);
        Invalidate(true);

        if (focused is { Length: > 0 } && _root.Controls.Find(focused, searchAllChildren: true).FirstOrDefault() is { } restored)
        {
            restored.Focus();
            // У полей имени и псевдонима курсор сохраняется сам (поле не пересоздаётся).
            if (restored is TextBox box && box != _nameBox && box != _aliasBox)
                box.SelectionStart = box.TextLength;
        }
        _rebuilding = false;
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
        if (_renaming is { } renaming && devices.All(device => device.DeviceId != renaming))
            _renaming = null;
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

    /// «Последняя синхронизация: 11:03 · текст от OFFICE-PC» / «… 11:05 · картинка отправлена».
    private string LastSyncText()
    {
        if (_lastSync() is not { } sync)
            return L("Синхронизаций пока не было", "No syncs yet");
        var time = sync.Time.Date == DateTime.Today ? sync.Time.ToString("t") : sync.Time.ToString("g");
        var what = (sync.Kind, sync.From) switch
        {
            (SyncKind.Image, { } from) => L($"картинка от {from}", $"image from {from}"),
            (SyncKind.Image, null) => L("картинка отправлена", "image sent"),
            (_, { } from) => L($"текст от {from}", $"text from {from}"),
            _ => L("текст отправлен", "text sent"),
        };
        return L($"Последняя синхронизация: {time} · {what}", $"Last sync: {time} · {what}");
    }

    /// Ширина содержимого карточки: ширина минус отступы Card (по 14 с каждой стороны).
    private int CardInnerWidth => Width96 - 2 * S(14);

    private Control DeviceCard(DeviceStatus device)
    {
        var card = NewCard();
        var table = NewTable(CardInnerWidth, P.Card, new Padding(0));

        var dotColor = device.Connected ? P.Success
            : device.Enabled && device.Problem is not null ? P.Warning
            : P.Disabled;
        var icon = new DeviceIcon(device.Type, P, _dpi, P.Card, dotColor)
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, S(2), S(10), 0),
        };

        var status = !device.Enabled ? L("синхронизация выключена", "sync is off")
            : device.Connected ? L("подключено", "connected")
            : device.Problem is { } problem ? FailureText(problem) : L("не в сети", "offline");
        // Картинки этому устройству не уходят: у него они выключены или старая версия Clipvey.
        if (device.Connected && device.Enabled && _node.ImagesEnabled && !device.AcceptsImages)
            status += L(" · только текст", " · text only");
        // Значок 28 + отступ 10; справа переключатель 44 + 8 и кнопка «…» 32 + 4.
        var textWidth = CardInnerWidth - S(28 + 10) - S(44 + 8) - S(32 + 4);
        var texts = NewColumn(P.Card);
        texts.Controls.Add(NewLabel(device.DisplayName, Theme.Text(14, _dpi, FontStyle.Bold), P.Text, P.Card, textWidth, new Padding(0)));
        if (device.Alias is not null)
        {
            // Псевдоним задан — настоящее имя устройства видно второй строкой.
            texts.Controls.Add(NewLabel(L($"Имя на устройстве: {device.Name}", $"Device name: {device.Name}"), Theme.Text(12, _dpi),
                P.SecondaryText, P.Card, textWidth, new Padding(0, S(2), 0, 0)));
        }
        texts.Controls.Add(NewLabel(status, Theme.Text(12, _dpi),
            device.Enabled && !device.Connected && device.Problem is not null ? P.Warning : P.SecondaryText,
            P.Card, textWidth, new Padding(0, S(2), 0, 0)));
        texts.Anchor = AnchorStyles.Left;

        var toggle = new ToggleSwitch(P, _dpi, P.Card, L($"Синхронизация с «{device.DisplayName}»", $"Sync with “{device.DisplayName}”"))
        {
            Checked = device.Enabled,
            Name = "toggle:" + device.DeviceId,
            Anchor = AnchorStyles.Right,
        };
        toggle.Toggled += (_, _) => _node.SetEnabled(device.DeviceId, toggle.Checked);

        // Действия с устройством — в меню «…», как на Mac: две кнопки-ссылки не помещались в строку.
        var more = NewButton("⋯", () => { }, ButtonKind.Subtle, P.Card, "more:" + device.DeviceId);
        more.AccessibleName = L($"Действия с «{device.DisplayName}»", $"Actions for “{device.DisplayName}”");
        more.MinimumSize = new Size(S(32), S(32));
        more.Margin = new Padding(S(4), 0, 0, 0);
        more.Click += (_, _) => ShowDeviceMenu(more, device);
        var controls = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = P.Card,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right,
        };
        toggle.Anchor = AnchorStyles.None;
        controls.Controls.Add(toggle);
        controls.Controls.Add(more);

        table.Controls.Add(icon, 0, 0);
        table.Controls.Add(texts, 1, 0);
        table.Controls.Add(controls, 2, 0);

        Control? actions;
        if (_renaming == device.DeviceId)
        {
            // Переименование прямо в карточке: MessageBox забрал бы фокус, и панель спряталась бы.
            var editor = NewColumn(P.Card);
            editor.Controls.Add(NewLabel(L("Имя на этом компьютере (другие устройства его не видят):", "Name on this PC (other devices don’t see it):"),
                Theme.Text(13, _dpi), P.Text, P.Card, textWidth + S(52), new Padding(0, S(8), 0, S(4))));
            StyleInput(_aliasBox, textWidth + S(52));
            _aliasBox.AccessibleName = L($"Новое имя для «{device.Name}»", $"New name for “{device.Name}”");
            _aliasBox.PlaceholderText = device.Name;
            editor.Controls.Add(_aliasBox);
            var buttons = new List<Control>
            {
                NewButton(L("Сохранить", "Save"), CommitAlias, ButtonKind.Accent, P.Card, "alias-save:" + device.DeviceId),
            };
            if (device.Alias is not null)
            {
                buttons.Add(NewButton(L("Сбросить", "Reset"), () =>
                {
                    _renaming = null;
                    _node.SetAlias(device.DeviceId, "");
                }, ButtonKind.Standard, P.Card, "alias-reset:" + device.DeviceId));
            }
            buttons.Add(NewButton(L("Отмена", "Cancel"), () =>
            {
                _renaming = null;
                RefreshContent();
            }, ButtonKind.Standard, P.Card, "alias-cancel:" + device.DeviceId));
            var row = NewRow(P.Card, [.. buttons]);
            row.Margin = new Padding(0, S(8), 0, 0);
            editor.Controls.Add(row);
            actions = editor;
        }
        else if (_confirmUnpair == device.DeviceId)
        {
            var question = NewColumn(P.Card);
            question.Controls.Add(NewLabel(L($"Разорвать связь с «{device.DisplayName}»? Чтобы снова синхронизироваться, их придётся связать заново.",
                    $"Unpair “{device.DisplayName}”? You’ll need to pair again to sync."),
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
            actions = null;
        }
        if (actions is not null)
        {
            table.Controls.Add(actions, 1, 1);
            table.SetColumnSpan(actions, 2);
        }

        card.Controls.Add(table);
        return card;
    }

    private void StartRename(DeviceStatus device)
    {
        _renaming = device.DeviceId;
        _confirmUnpair = null;
        _aliasBox.Text = device.DisplayName;
        RefreshContent();
        _aliasBox.Focus();
        _aliasBox.SelectAll();
    }

    /// Сохранить псевдоним. Пустой или совпадающий с настоящим именем — сброс.
    private void CommitAlias()
    {
        if (_renaming is not { } deviceId)
            return;
        var device = _node.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        _renaming = null;
        if (device is null)
        {
            RefreshContent();
            return;
        }
        var alias = ClipveyNode.NormalizeName(_aliasBox.Text);
        if (alias == device.Name)
            alias = "";
        if (alias == (device.Alias ?? ""))
            RefreshContent();
        else
            _node.SetAlias(deviceId, alias);
    }

    /// Сохранить своё имя (Enter, потеря фокуса, скрытие панели). Пустое — имя компьютера. Без изменений — ничего.
    private void CommitName()
    {
        if (!_nameDirty)
            return;
        _nameDirty = false;
        var name = ClipveyNode.NormalizeName(_nameBox.Text);
        var effective = ClipveyNode.NormalizeName(name.Length > 0 ? name : Environment.MachineName);
        if (effective != _node.Name || (name.Length == 0) != (AppSettings.DeviceName is null))
            _rename(name);
        SetNameText(_node.Name);
        _nameBox.SelectionStart = _nameBox.TextLength;
    }

    private void SetNameText(string text)
    {
        if (_nameBox.Text == text)
            return;
        _settingName = true;
        _nameBox.Text = text;
        _settingName = false;
    }

    private void StyleInput(TextBox box, int width)
    {
        box.Font = Theme.Text(14, _dpi);
        box.BackColor = P.Input;
        box.ForeColor = P.Text;
        box.Width = width;
        box.Margin = new Padding(0);
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
            column.Controls.Add(WithIcon(incoming.PeerType, Strong(L($"Связывание с «{incoming.PeerName}»", $"Pairing with “{incoming.PeerName}”"))));
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
                var candidateIcon = new DeviceIcon(candidate.Type, P, _dpi, P.Card, dot: null)
                {
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 0, S(10), 0),
                };
                row.Controls.Add(candidateIcon, 0, 0);
                var name = NewLabel(candidate.Name, Theme.Text(14, _dpi, FontStyle.Bold), P.Text, P.Card, width - S(110 + 38), new Padding(0));
                name.Anchor = AnchorStyles.Left;
                name.Margin = new Padding(0);
                var pair = NewButton(L("Связать", "Pair"), () => StartOutgoing(candidate), ButtonKind.Accent, P.Card,
                    "candidate:" + (candidate.DeviceId ?? candidate.Name));
                pair.Anchor = AnchorStyles.Right;
                pair.Margin = new Padding(0);
                row.Controls.Add(name, 1, 0);
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

    /// Значок типа устройства слева от заголовка.
    private Control WithIcon(DeviceType type, Label label)
    {
        var row = NewTable(CardInnerWidth, P.Card, new Padding(0, 0, 0, S(4)));
        var icon = new DeviceIcon(type, P, _dpi, P.Card, dot: null)
        {
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, S(10), 0),
        };
        label.MaximumSize = new Size(CardInnerWidth - S(38), 0);
        label.Margin = new Padding(0);
        label.Anchor = AnchorStyles.Left;
        row.Controls.Add(icon, 0, 0);
        row.Controls.Add(label, 1, 0);
        return row;
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
        var column = NewColumn(P.Card);
        // Слева подпись, справа переключатель (44 + 8); у строки «Язык» справа кнопка пошире.
        var labelWidth = CardInnerWidth - S(44 + 8);

        // Своё имя: другие устройства видят его в списке и при связывании.
        var nameTitle = L("Имя этого компьютера", "This PC’s name");
        column.Controls.Add(NewLabel(nameTitle, Theme.Text(14, _dpi), P.Text, P.Card, CardInnerWidth, new Padding(0, 0, 0, S(4))));
        StyleInput(_nameBox, CardInnerWidth);
        _nameBox.AccessibleName = nameTitle;
        _nameBox.PlaceholderText = Environment.MachineName;
        if (!_nameDirty)
            SetNameText(_node.Name);
        column.Controls.Add(_nameBox);
        column.Controls.Add(NewLabel(L("Enter — сохранить. Пустое поле — имя компьютера в Windows.",
                "Press Enter to save. Leave empty to use the Windows computer name."),
            Theme.Text(12, _dpi), P.SecondaryText, P.Card, CardInnerWidth, new Padding(0, S(4), 0, S(12))));

        var table = NewTable(CardInnerWidth, P.Card, new Padding(0));

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
        table.SetColumnSpan(autostartLabel, 2);
        table.Controls.Add(autostart, 2, 0);

        var imagesText = L("Передавать картинки", "Share images");
        var imagesLabels = NewColumn(P.Card);
        imagesLabels.Margin = new Padding(0, S(10), 0, 0);
        imagesLabels.Anchor = AnchorStyles.Left;
        imagesLabels.Controls.Add(NewLabel(imagesText, Theme.Text(14, _dpi), P.Text, P.Card, CardInnerWidth - S(60), new Padding(0)));
        imagesLabels.Controls.Add(NewLabel(L("До 20 МБ, только устройствам, где картинки тоже включены",
                "Up to 20 MB, only with devices that have images turned on too"),
            Theme.Text(12, _dpi), P.SecondaryText, P.Card, CardInnerWidth - S(60), new Padding(0, S(2), 0, 0)));
        var images = new ToggleSwitch(P, _dpi, P.Card, imagesText)
        {
            Checked = _node.ImagesEnabled,
            Name = "images",
            Anchor = AnchorStyles.Right,
            Margin = new Padding(S(8), S(10), 0, 0),
        };
        images.Toggled += (_, _) => _setImagesEnabled(images.Checked);
        table.Controls.Add(imagesLabels, 0, 1);
        table.SetColumnSpan(imagesLabels, 2);
        table.Controls.Add(images, 2, 1);

        var languageLabel = NewLabel(L("Язык", "Language"), Theme.Text(14, _dpi), P.Text, P.Card, CardInnerWidth - S(160), new Padding(0, S(10), 0, 0));
        languageLabel.Anchor = AnchorStyles.Left;
        var current = TrayApplication.LanguageChoices().First(choice => choice.Value == Setting).Title;
        var language = NewButton(current + "  ▾", () => { }, ButtonKind.Standard, P.Card, "language");
        language.AccessibleName = L($"Язык: {current}", $"Language: {current}");
        language.Anchor = AnchorStyles.Right;
        language.Margin = new Padding(0, S(10), 0, 0);
        language.Click += (_, _) => ShowLanguageMenu(language);
        table.Controls.Add(languageLabel, 0, 2);
        table.SetColumnSpan(languageLabel, 2);
        table.Controls.Add(language, 2, 2);

        column.Controls.Add(table);
        card.Controls.Add(column);
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
    private void ShowDeviceMenu(Control anchor, DeviceStatus device)
    {
        var menu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(P) };
        var rename = new ToolStripMenuItem(L("Переименовать…", "Rename…"));
        rename.Click += (_, _) => BeginInvoke(() => StartRename(device));
        menu.Items.Add(rename);
        if (device.Alias is not null)
        {
            var reset = new ToolStripMenuItem(L($"Вернуть имя «{device.Name}»", $"Restore name “{device.Name}”"));
            reset.Click += (_, _) => BeginInvoke(() => _node.SetAlias(device.DeviceId, ""));
            menu.Items.Add(reset);
        }
        menu.Items.Add(new ToolStripSeparator());
        var unpair = new ToolStripMenuItem(L("Разорвать связь…", "Unpair…")) { ForeColor = P.Danger };
        unpair.Click += (_, _) => BeginInvoke(() =>
        {
            _confirmUnpair = device.DeviceId;
            _renaming = null;
            RefreshContent();
        });
        menu.Items.Add(unpair);
        menu.Closed += (_, _) =>
        {
            BeginInvoke(menu.Dispose);
            ReactivateAfterMenu();
        };
        menu.Show(anchor, new Point(anchor.Width, anchor.Height), ToolStripDropDownDirection.BelowLeft);
    }

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
        menu.Closed += (_, _) =>
        {
            BeginInvoke(menu.Dispose);
            ReactivateAfterMenu();
        };
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
}
