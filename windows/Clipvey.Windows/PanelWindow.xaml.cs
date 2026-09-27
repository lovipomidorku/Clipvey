using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipvey.Core;
using static Clipvey.Windows.Localization;
using Forms = System.Windows.Forms;

namespace Clipvey.Windows;

/// Панель Clipvey у значка в трее: связанные устройства (с выключателями), связывание новых — в обеих ролях,
/// настройки и обновления. Не видна в панели задач, прячется, когда активной становится другая программа
/// (кроме как во время связывания). Закрытие только прячет панель: программа продолжает работать в трее.
///
/// Окно создаётся один раз. RefreshContent обновляет тексты, видимость и списки на месте:
/// поля ввода не пересоздаются, фокус и набранный текст сохраняются.
/// Положение считается в физических пикселях (PanelPlacement) и ставится через SetWindowPos —
/// так оно верно на мониторах с разным масштабом.
internal sealed partial class PanelWindow : Window
{
    private readonly TrayApplication _app;
    private readonly ObservableCollection<DeviceItem> _devices = [];
    private readonly ObservableCollection<CandidateItem> _candidates = [];
    private readonly DispatcherTimer _countdown;
    private IntPtr _hwnd;

    /// Экран, у которого открыта панель: при изменении размера она остаётся на нём.
    private Forms.Screen? _screen;

    private bool _quitting;
    private bool _loggedLook;
    private string _result = "";

    // Исходящее связывание (роль I): этот компьютер вводит код с экрана другого устройства.
    private string? _outgoingPeer;
    private TaskCompletionSource<string?>? _codeRequest;
    private bool _codeAccepted;
    private CancellationTokenSource? _outgoingCancel;

    /// Устройство, которое переименовывается, и устройство, для которого показан вопрос «Разорвать связь?».
    /// Оба встроены в панель: MessageBox забрал бы фокус, и панель спряталась бы.
    private string? _renaming;
    private string? _confirmUnpair;

    /// Имя в поле «Имя этого компьютера» изменено пользователем и ещё не сохранено.
    private bool _nameDirty;

    /// Идёт программное обновление элементов: их события — не действия пользователя.
    private bool _updating;

    /// Язык, на котором заполнен список «Язык».
    private bool? _languageListRussian;

    /// Открыта страница настроек (иначе главная).
    private bool _settingsPage;

    /// Когда панель спряталась из-за ухода в другую программу (Environment.TickCount64).
    public long HiddenAt { get; private set; }

    private ClipveyNode Node => _app.Node;
    private Updater Updater => _app.Updater;

    public PanelWindow(TrayApplication app)
    {
        _app = app;
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        CandidateList.ItemsSource = _candidates;
        AppIconImage.Source = LoadIcon();
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateCountdown(), Dispatcher);
        _countdown.Stop();
        DataObject.AddPastingHandler(CodeBox, OnCodePaste);
        SourceInitialized += (_, _) => OnSourceInitialized();
        IsVisibleChanged += OnVisibleChanged;
        Theme.Changed += ApplyBackdrop;
        Localization.Changed += RefreshContent;
    }

    private static BitmapSource? LoadIcon()
    {
        try
        {
            if (AppIcon.Png(64) is not { } png)
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(png);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось загрузить значок для панели: {e.Message}");
            return null;
        }
    }

    // MARK: - Окно

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Не показывать в Alt+Tab.
        const int GwlStyle = -16;
        const int GwlExStyle = -20;
        const int WsExToolWindow = 0x00000080;
        const int WsSysMenu = 0x00080000;
        SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExToolWindow);
        // Рамка окна (WS_CAPTION) остаётся — по ней DWM рисует тень и скругление, — а без системного меню
        // DWM не рисует кнопку закрытия. Панель прячется кликом мимо, Esc и кликом по значку.
        SetWindowLong(_hwnd, GwlStyle, GetWindowLong(_hwnd, GwlStyle) & ~WsSysMenu);
        if (HwndSource.FromHwnd(_hwnd) is { } source)
        {
            // Прозрачный фон WPF: сквозь него видна подложка DWM.
            if (source.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
            source.AddHook(WndProc);
        }
        var rounded = Dwm.TryRoundCorners(_hwnd);
        Log.Write(rounded ? "Панель: углы скруглены (DWM)" : "Панель: DWM не скругляет углы");
        ApplyBackdrop();
    }

    private bool? _acrylic;

    /// Подложка Acrylic и цвет рамки DWM по теме. Без Acrylic (Windows 10, ранние сборки 11) — однотонный фон темы.
    private void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Dwm.TrySetDark(_hwnd, Theme.IsDark);
        var acrylic = Dwm.TrySetAcrylic(_hwnd);
        Tint.Visibility = acrylic && !Theme.IsDark ? Visibility.Visible : Visibility.Collapsed;
        if (acrylic)
            Background = Brushes.Transparent;
        else
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        if (_acrylic != acrylic)
            Log.Write(acrylic ? "Панель: подложка Acrylic" : "Панель: Acrylic недоступен, фон однотонный");
        _acrylic = acrylic;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSysCommand = 0x0112;
        const int WmWindowPosChanging = 0x0046;
        const int ScMaximize = 0xF030;
        const int ScMinimize = 0xF020;
        const int ScMove = 0xF010;
        const int ScSize = 0xF000;
        // Панель не разворачивается, не сворачивается и не двигается (Win+↑, Alt+Пробел).
        if (message == WmSysCommand && ((int)wParam & 0xFFF0) is ScMaximize or ScMinimize or ScMove or ScSize)
            handled = true;
        // Панель растёт и сжимается вместе с содержимым (и при смене DPI): угол у панели задач остаётся на месте.
        // Положение подставляется в само изменение размера — без прыжка через кадр.
        if (message == WmWindowPosChanging && _screen is { } screen)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            if ((pos.Flags & SwpNoSize) == 0 && pos.Width > 0 && pos.Height > 0)
            {
                var location = PanelPlacement.Locate(new System.Drawing.Size(pos.Width, pos.Height), WindowDpi, screen);
                pos.X = location.X;
                pos.Y = location.Y;
                pos.Flags &= ~SwpNoMove;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private int WindowDpi => _hwnd != IntPtr.Zero && GetDpiForWindow(_hwnd) is var dpi and > 0 ? (int)dpi : 96;

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    /// Идёт связывание: открыт режим, пришёл запрос или этот компьютер вводит код.
    private bool PairingInProgress => Node.IsPairingMode || Node.IncomingPairing is not null || _outgoingPeer is not null;

    /// Показать панель у области уведомлений и отдать ей фокус. Страница — настройки, если просили
    /// или идёт связывание (блок связывания там), иначе главная.
    public void ShowPanel(bool settings = false)
    {
        if (settings || PairingInProgress)
            _settingsPage = true;
        else if (!IsVisible)
            _settingsPage = false;
        _screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var dpi = PanelPlacement.CursorMonitorDpi() ?? 96;
        // Панель не выше рабочей области экрана (в DIP этого экрана), дальше — прокрутка.
        MaxHeight = Math.Max(200, _screen.WorkingArea.Height * 96.0 / dpi - 24);
        if (_hwnd == IntPtr.Zero)
            new WindowInteropHelper(this).EnsureHandle();
        _showing = true;
        RefreshContent();
        _showing = false;
        UpdateLayout();
        Place();
        if (!IsVisible)
            Show();
        Place();
        Activate();
        SetForegroundWindow(_hwnd);
        _countdown.Start();
        LogLookOnce(dpi);
    }

    private bool _showing;

    public void HidePanel()
    {
        if (IsVisible)
            Hide();
    }

    /// Закрыть по-настоящему (выход из программы).
    public void ClosePanel()
    {
        _quitting = true;
        Theme.Changed -= ApplyBackdrop;
        Localization.Changed -= RefreshContent;
        CommitName();
        Close();
    }

    /// Прямоугольник панели на экране в физических пикселях (null — окна ещё нет).
    public System.Drawing.Rectangle? ScreenBounds =>
        _hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var rect) ? System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;

    /// Поставить панель в угол у области уведомлений. Размер окна — из GetWindowRect (физические пиксели).
    private void Place()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect))
            return;
        var screen = _screen ?? Forms.Screen.FromPoint(Forms.Cursor.Position);
        var size = new System.Drawing.Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        var location = PanelPlacement.Locate(size, WindowDpi, screen);
        if (location.X == rect.Left && location.Y == rect.Top)
            return;
        SetWindowPos(_hwnd, IntPtr.Zero, location.X, location.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void LogLookOnce(int dpi)
    {
        if (_loggedLook)
            return;
        _loggedLook = true;
        var families = Fonts.SystemFontFamilies.Select(family => family.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Has(string name) => families.Contains(name) ? name : $"нет {name}";
        Log.Write($"Панель: DPI {dpi} (окна {GetDpiForWindow(_hwnd)}), тема {(Theme.IsDark ? "тёмная" : "светлая")}");
        Log.Write($"Шрифт: {Has("Segoe UI Variable Text")}, значки: {Has("Segoe Fluent Icons")}, {Has("Segoe MDL2 Assets")}");
        // Ресурсы темы с неожиданным типом не рисуются молча — проверяем, что это кисти.
        string[] keys = ["ToggleSwitchFillOn", "ToggleSwitchFillOff", "ToggleSwitchStrokeOff", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOff",
            "CardBackgroundFillColorDefaultBrush", "SystemFillColorSuccessBrush", "SystemFillColorCautionBrush", "SolidBackgroundFillColorBaseBrush"];
        var wrong = keys.Where(key => TryFindResource(key) is not Brush).Select(key => $"{key}={TryFindResource(key)?.GetType().Name ?? "нет"}").ToList();
        if (wrong.Count > 0)
            Log.Write($"Панель: ресурсы темы не кисти: {string.Join(", ", wrong)}");
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible && !IsPairingActive)
            Dispatcher.BeginInvoke(HideIfFocusLeftApp, DispatcherPriority.Background);
    }

    /// Прятать панель, только если активной стала другая программа. Меню «⋯» и список языков — всплывающие окна
    /// самого Clipvey: пока они открыты, панель остаётся на месте.
    private async void HideIfFocusLeftApp()
    {
        await Task.Delay(120);
        if (!IsVisible || IsPairingActive || IsActive)
            return;
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        if (foreground == IntPtr.Zero || process == (uint)Environment.ProcessId)
            return;
        HiddenAt = Environment.TickCount64;
        Hide();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CommitName();
            _countdown.Stop();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Esc в открытом списке или меню обрабатывают они сами (e.Handled).
        if (e.Handled || e.Key != Key.Escape)
            return;
        // Esc во время правки отменяет правку, а не прячет панель.
        if (_renaming is not null)
        {
            _renaming = null;
            RefreshContent();
        }
        else if (NameBox.IsKeyboardFocused && _nameDirty)
        {
            _nameDirty = false;
            SetNameText(Node.Name);
            NameBox.SelectAll();
        }
        else
        {
            Hide();
        }
        e.Handled = true;
    }

    /// Связывание идёт — панель не прячется при уходе в другую программу, чтобы код оставался на экране.
    private bool IsPairingActive => Node.IncomingPairing is not null || _outgoingPeer is not null;

    public void ShowResult(string text)
    {
        _result = text;
        RefreshContent();
    }

    // MARK: - Страницы

    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowPage(settings: true);

    private void OnBack(object sender, RoutedEventArgs e) => ShowPage(settings: false);

    private void ShowPage(bool settings)
    {
        if (_settingsPage == settings)
            return;
        // Уходя из настроек, сохранить набранное имя.
        if (!settings)
            CommitName();
        _settingsPage = settings;
        RefreshContent();
        // Фокус — на кнопку возврата: с клавиатуры можно сразу вернуться (Enter/пробел).
        Dispatcher.BeginInvoke(() => (settings ? BackButton : SettingsButton).Focus(), DispatcherPriority.Loaded);
    }

    // MARK: - Содержимое

    public void RefreshContent()
    {
        // Скрытая панель обновится при показе (список устройств читается с диска).
        if (!IsVisible && !_showing)
            return;
        _updating = true;
        try
        {
            MainPage.Visibility = Shown(!_settingsPage);
            SettingsPage.Visibility = Shown(_settingsPage);
            var settingsText = L("Настройки", "Settings");
            SettingsPageTitle.Text = settingsText;
            System.Windows.Automation.AutomationProperties.SetName(SettingsButton, settingsText);
            SettingsButton.ToolTip = settingsText;
            var backText = L("Назад", "Back");
            System.Windows.Automation.AutomationProperties.SetName(BackButton, backText);
            BackButton.ToolTip = backText;
            RefreshUpdateBanner();
            RefreshDevices();
            RefreshPairing();
            RefreshSettings();
            RefreshUpdates();
            Footer.Text = L($"Этот компьютер: {Node.Name} · порт {Node.Port}", $"This PC: {Node.Name} · port {Node.Port}");
        }
        finally
        {
            _updating = false;
        }
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshDevices()
    {
        var devices = Node.Devices;
        if (_renaming is { } renaming && devices.All(device => device.DeviceId != renaming))
            _renaming = null;
        if (_confirmUnpair is { } unpair && devices.All(device => device.DeviceId != unpair))
            _confirmUnpair = null;
        var connected = devices.Count(device => device.Connected);
        DevicesTitle.Text = L("Устройства", "Devices");
        DevicesNote.Text = devices.Count == 0 ? "" : L($"подключено {connected} из {devices.Count}", $"{connected} of {devices.Count} connected");
        NoDevicesCard.Visibility = Shown(devices.Count == 0);
        NoDevicesText.Text = L("Нет связанных устройств. Добавить устройство можно в настройках.",
            "No paired devices. You can add a device in Settings.");
        NoDevicesSettings.Content = L("Открыть настройки", "Open Settings");
        LastSyncText.Visibility = Shown(devices.Count > 0);
        LastSyncText.Text = UiText.LastSync(_app.LastSync);

        Sync(_devices, devices, device => device.DeviceId, id => new DeviceItem(id), (item, device) =>
            item.Update(device, Node.ImagesEnabled, _renaming == device.DeviceId, _confirmUnpair == device.DeviceId));
    }

    /// Привести список строк к данным узла, не пересоздавая оставшиеся строки.
    private static void Sync<TItem, TData>(ObservableCollection<TItem> items, IReadOnlyList<TData> data, Func<TData, string> key,
        Func<string, TItem> create, Action<TItem, TData> update) where TItem : PanelItem
    {
        var keys = data.Select(key).ToList();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(KeyOf(items[i])))
                items.RemoveAt(i);
        }
        for (var i = 0; i < data.Count; i++)
        {
            var existing = items.Select((item, index) => (item, index)).FirstOrDefault(pair => KeyOf(pair.item) == keys[i]);
            TItem item;
            if (existing.item is null)
            {
                item = create(keys[i]);
                items.Insert(i, item);
            }
            else
            {
                item = existing.item;
                if (existing.index != i)
                    items.Move(existing.index, i);
            }
            update(item, data[i]);
        }

        static string KeyOf(TItem item) => item switch
        {
            DeviceItem device => device.Id,
            CandidateItem candidate => candidate.Key,
            _ => "",
        };
    }

    private static DeviceItem ItemOf(object sender) => (DeviceItem)((FrameworkElement)sender).DataContext;

    private void OnDeviceToggle(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        Node.SetEnabled(ItemOf(sender).Id, toggle.IsChecked == true);
    }

    private void OnDeviceMore(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var device = ItemOf(sender).Device;
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Custom,
            // Правым краем к правому краю кнопки, под ней (или над ней, если внизу нет места).
            CustomPopupPlacementCallback = (popup, target, _) =>
            [
                new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 2), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 2), PopupPrimaryAxis.Horizontal),
            ],
        };
        menu.Items.Add(MenuItem(L("Переименовать…", "Rename…"), "menu-rename", () => StartRename(device)));
        if (device.Alias is not null)
            menu.Items.Add(MenuItem(L($"Вернуть имя «{device.Name}»", $"Restore name “{device.Name}”"), "menu-restore",
                () => Node.SetAlias(device.DeviceId, "")));
        menu.Items.Add(new Separator());
        var unpair = MenuItem(L("Разорвать связь…", "Unpair…"), "menu-unpair", () =>
        {
            _confirmUnpair = device.DeviceId;
            _renaming = null;
            RefreshContent();
        });
        unpair.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
        menu.Items.Add(unpair);
        menu.IsOpen = true;
    }

    private MenuItem MenuItem(string header, string automationId, Action onClick)
    {
        var item = new MenuItem { Header = header };
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, automationId);
        // Действие — после закрытия меню: оно меняет содержимое панели.
        item.Click += (_, _) => Dispatcher.BeginInvoke(onClick, DispatcherPriority.Background);
        return item;
    }

    private void StartRename(DeviceStatus device)
    {
        _renaming = device.DeviceId;
        _confirmUnpair = null;
        if (_devices.FirstOrDefault(item => item.Id == device.DeviceId) is { } item)
            item.AliasDraft = device.DisplayName;
        RefreshContent();
    }

    /// Поле псевдонима появилось — перевести в него фокус и выделить имя.
    private void OnAliasVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnAliasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitAlias(ItemOf(sender));
            e.Handled = true;
        }
    }

    private void OnAliasSave(object sender, RoutedEventArgs e) => CommitAlias(ItemOf(sender));

    private void OnAliasReset(object sender, RoutedEventArgs e)
    {
        _renaming = null;
        Node.SetAlias(ItemOf(sender).Id, "");
    }

    private void OnAliasCancel(object sender, RoutedEventArgs e)
    {
        _renaming = null;
        RefreshContent();
    }

    /// Сохранить псевдоним. Пустой или совпадающий с настоящим именем — сброс.
    private void CommitAlias(DeviceItem item)
    {
        _renaming = null;
        var device = Node.Devices.FirstOrDefault(d => d.DeviceId == item.Id);
        if (device is null)
        {
            RefreshContent();
            return;
        }
        var alias = ClipveyNode.NormalizeName(item.AliasDraft);
        if (alias == device.Name)
            alias = "";
        if (alias == (device.Alias ?? ""))
            RefreshContent();
        else
            Node.SetAlias(device.DeviceId, alias);
    }

    private void OnUnpairYes(object sender, RoutedEventArgs e)
    {
        _confirmUnpair = null;
        Node.Unpair(ItemOf(sender).Id);
    }

    private void OnUnpairNo(object sender, RoutedEventArgs e)
    {
        _confirmUnpair = null;
        RefreshContent();
    }

    // MARK: - Связывание

    private void RefreshPairing()
    {
        PairingTitle.Text = L("Связывание", "Pairing");
        // На главной во время связывания — строка-переход к нему.
        PairingBar.Visibility = Shown(PairingInProgress);
        PairingBarText.Text = L("Идёт связывание — продолжить", "Pairing in progress — continue");
        var incoming = Node.IncomingPairing;
        var mode = Node.IsPairingMode;
        PairIdle.Visibility = Shown(incoming is null && _outgoingPeer is null && !mode);
        PairMode.Visibility = Shown(incoming is null && _outgoingPeer is null && mode);
        PairIncoming.Visibility = Shown(incoming is not null);
        PairOutgoing.Visibility = Shown(incoming is null && _outgoingPeer is not null);
        PairResult.Visibility = Shown(_result.Length > 0);
        PairResult.Text = _result;

        PairStart.Content = L("Добавить устройство", "Add Device…");

        PairModeText.Text = L("Нажмите «Связать» и на другом устройстве. Затем выберите его здесь — или этот компьютер там.",
            "Select Pair on the other device too. Then choose it here, or choose this PC there.");
        var candidates = mode ? Node.PairingCandidates : [];
        Searching.Visibility = Shown(candidates.Count == 0);
        SearchingText.Text = L("Поиск устройств…", "Looking for devices…");
        Sync(_candidates, candidates, CandidateItem.KeyOf, key => new CandidateItem(key), (item, device) => item.Update(device));
        CandidateList.Visibility = Shown(candidates.Count > 0);
        PairClose.Content = L("Закрыть", "Close");

        if (incoming is not null)
        {
            // Роль R: этот компьютер показывает код.
            IncomingGlyph.Text = UiText.Glyph(incoming.PeerType);
            System.Windows.Automation.AutomationProperties.SetName(IncomingGlyph, UiText.Describe(incoming.PeerType));
            IncomingTitle.Text = L($"Связывание с «{incoming.PeerName}»", $"Pairing with “{incoming.PeerName}”");
            IncomingCode.Text = incoming.Code is { } code ? $"{code[..3]} {code[3..]}" : "…";
            IncomingText.Text = incoming.Verified
                ? L($"«{incoming.PeerName}» подтвердил код ✓ Нажмите «Готово».", $"“{incoming.PeerName}” confirmed the code ✓ Select Done.")
                : L($"Введите этот код на «{incoming.PeerName}».", $"Enter this code on “{incoming.PeerName}”.");
            IncomingDone.Content = L("Готово", "Done");
            IncomingDone.IsEnabled = incoming.Verified;
            IncomingCancel.Content = L("Отмена", "Cancel");
        }

        if (_outgoingPeer is { } peer)
        {
            // Роль I: этот компьютер вводит код с экрана другого устройства.
            OutgoingTitle.Text = L($"Связывание с «{peer}»", $"Pairing with “{peer}”");
            OutgoingText.Text = _codeRequest is not null ? L($"Введите код с экрана «{peer}»:", $"Enter the code shown on “{peer}”:")
                : _codeAccepted ? L($"Код верный ✓ Нажмите «Готово» на «{peer}».", $"Code is correct ✓ Select Done on “{peer}”.")
                : L("Подключение…", "Connecting…");
            CodeEntry.Visibility = Shown(_codeRequest is not null);
            System.Windows.Automation.AutomationProperties.SetName(CodeBox, L("Код связывания", "Pairing code"));
            CodeSubmit.Content = L("Подтвердить", "Confirm");
            CodeSubmit.IsEnabled = CodeBox.Text.Length == 6;
            OutgoingCancel.Content = L("Отмена", "Cancel");
        }
        UpdateCountdown();
    }

    /// Обратный отсчёт до закрытия режима связывания — пока код ещё не показан.
    private void UpdateCountdown()
    {
        var incoming = Node.IncomingPairing;
        var left = _app.PairingDeadline is { } deadline ? deadline - DateTime.UtcNow : TimeSpan.Zero;
        var show = Node.IsPairingMode && left > TimeSpan.Zero && incoming?.Code is null;
        Countdown.Visibility = Shown(show);
        if (show)
        {
            var text = $"{(int)left.TotalMinutes}:{left.Seconds:00}";
            Countdown.Text = L($"осталось {text}", $"{text} left");
        }
        // Режим закрылся по времени: узел может об этом не сообщить — перестраиваемся сами.
        if (!_updating && PairMode.Visibility == Visibility.Visible && !Node.IsPairingMode)
            RefreshContent();
    }

    private void OnPairingStart(object sender, RoutedEventArgs e)
    {
        _result = "";
        _app.StartPairingMode();
    }

    private void OnPairingClose(object sender, RoutedEventArgs e) => Node.StopPairingMode();

    private void OnIncomingDone(object sender, RoutedEventArgs e) => Node.IncomingPairing?.Confirm();

    private void OnIncomingCancel(object sender, RoutedEventArgs e) => Node.IncomingPairing?.Cancel();

    private void OnCandidatePair(object sender, RoutedEventArgs e) =>
        StartOutgoing(((CandidateItem)((FrameworkElement)sender).DataContext).Device);

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
            await Node.PairWithAsync(candidate, RequestCodeAsync, () => Dispatcher.BeginInvoke(() =>
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
        Dispatcher.BeginInvoke(() =>
        {
            _codeRequest = request;
            CodeBox.Text = "";
            RefreshContent();
            Dispatcher.BeginInvoke(() => CodeBox.Focus(), DispatcherPriority.Loaded);
        });
        return request.Task;
    }

    private void OnCodePreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnCodePaste(object sender, DataObjectPastingEventArgs e)
    {
        // Вставка: оставить только цифры («123 456» → «123456»).
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string text)
        {
            var digits = new string([.. text.Where(char.IsAsciiDigit)]);
            e.CancelCommand();
            CodeBox.SelectedText = digits[..Math.Min(digits.Length, 6 - CodeBox.Text.Length + CodeBox.SelectionLength)];
            CodeBox.CaretIndex = CodeBox.SelectionStart + CodeBox.SelectionLength;
            CodeBox.SelectionLength = 0;
        }
    }

    private void OnCodeChanged(object sender, TextChangedEventArgs e) => CodeSubmit.IsEnabled = CodeBox.Text.Length == 6;

    private void OnCodeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitCode();
            e.Handled = true;
        }
    }

    private void OnCodeSubmit(object sender, RoutedEventArgs e) => SubmitCode();

    private void SubmitCode()
    {
        if (_codeRequest is not { } request || CodeBox.Text.Length != 6)
            return;
        _codeRequest = null;
        request.TrySetResult(CodeBox.Text);
        RefreshContent();
    }

    private void OnOutgoingCancel(object sender, RoutedEventArgs e)
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

    private void RefreshSettings()
    {
        GeneralTitle.Text = L("Основные", "General");
        NameTitle.Text = L("Имя этого компьютера", "This PC’s name");
        System.Windows.Automation.AutomationProperties.SetName(NameBox, NameTitle.Text);
        if (!_nameDirty)
            SetNameText(Node.Name);

        AutostartText.Text = L("Запускать при входе в Windows", "Start with Windows");
        System.Windows.Automation.AutomationProperties.SetName(AutostartSwitch, AutostartText.Text);
        AutostartSwitch.IsChecked = Autostart.SafeIsEnabled();

        ImagesText.Text = L("Передавать картинки", "Share images");
        System.Windows.Automation.AutomationProperties.SetName(ImagesSwitch, ImagesText.Text);
        ImagesSwitch.IsChecked = Node.ImagesEnabled;

        FilesText.Text = L("Передавать файлы", "Share files");
        System.Windows.Automation.AutomationProperties.SetName(FilesSwitch, FilesText.Text);
        FilesSwitch.IsChecked = Node.FilesEnabled;

        LanguageText.Text = L("Язык", "Language");
        System.Windows.Automation.AutomationProperties.SetName(LanguageBox, LanguageText.Text);
        if (_languageListRussian != IsRussian || LanguageBox.Items.Count == 0)
        {
            // Названия вариантов зависят от языка — список заполняется заново только при его смене.
            _languageListRussian = IsRussian;
            LanguageBox.Items.Clear();
            foreach (var (value, title) in TrayApplication.LanguageChoices())
                LanguageBox.Items.Add(new ComboBoxItem { Content = title, Tag = value });
        }
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (AppLanguage)item.Tag == Setting);
    }

    private void OnAutostartClick(object sender, RoutedEventArgs e) =>
        AutostartSwitch.IsChecked = Autostart.SafeSet(AutostartSwitch.IsChecked == true);

    private void OnImagesClick(object sender, RoutedEventArgs e) => _app.SetImagesEnabled(ImagesSwitch.IsChecked == true);

    /// По Checked/Unchecked, а не Click: так срабатывает и переключение через UI Automation (экранный диктор).
    /// Программная установка в RefreshSettings идёт под _updating и не считается действием пользователя.
    private void OnFilesChanged(object sender, RoutedEventArgs e)
    {
        if (!_updating)
            _app.SetFilesEnabled(FilesSwitch.IsChecked == true);
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LanguageBox.SelectedItem is not ComboBoxItem { Tag: AppLanguage value } || value == Setting)
            return;
        // Смена языка перезаполняет этот же список — откладываем её до конца обработки выбора.
        Dispatcher.BeginInvoke(() => Localization.Set(value), DispatcherPriority.Background);
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingName)
            _nameDirty = true;
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitName();
            e.Handled = true;
        }
    }

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitName();

    private bool _settingName;

    /// Сохранить своё имя (Enter, уход из поля, скрытие панели). Пустое — имя компьютера. Без изменений — ничего.
    private void CommitName()
    {
        if (!_nameDirty)
            return;
        _nameDirty = false;
        var name = ClipveyNode.NormalizeName(NameBox.Text);
        var effective = ClipveyNode.NormalizeName(name.Length > 0 ? name : Environment.MachineName);
        if (effective != Node.Name || (name.Length == 0) != (AppSettings.DeviceName is null))
            _app.Rename(name);
        SetNameText(Node.Name);
        NameBox.CaretIndex = NameBox.Text.Length;
    }

    private void SetNameText(string text)
    {
        if (NameBox.Text == text)
            return;
        _settingName = true;
        NameBox.Text = text;
        _settingName = false;
    }

    // MARK: - Обновления

    private bool Busy => Updater.Phase is UpdatePhase.Downloading or UpdatePhase.Installing;

    private string ProgressText => Updater.Phase == UpdatePhase.Downloading
        ? L("Загрузка и проверка…", "Downloading and verifying…")
        : L("Установка…", "Installing…");

    private void RefreshUpdateBanner()
    {
        UpdateBanner.Visibility = Shown(Updater.ShowsBanner);
        if (!Updater.ShowsBanner || Updater.Available is not { } release)
            return;
        BannerTitle.Text = L($"Доступна версия {release.Version} — обновить?", $"Version {release.Version} is available. Update?");
        BannerProgress.Visibility = Shown(Busy);
        BannerProgress.Text = ProgressText;
        BannerButtons.Visibility = Shown(!Busy);
        BannerInstall.Content = L("Обновить", "Update");
        BannerInstall.IsEnabled = Updater.Phase == UpdatePhase.Idle;
        BannerLater.Content = L("Позже", "Later");
        BannerNotice.Visibility = Shown(Updater.Notice is not null);
        BannerNotice.Text = Updater.NoticeText;
        SetNoticeColor(BannerNotice);
    }

    private void RefreshUpdates()
    {
        UpdatesTitle.Text = L("Обновления", "Updates");
        AutoUpdateText.Text = L("Проверять автоматически", "Check automatically");
        System.Windows.Automation.AutomationProperties.SetName(AutoUpdateSwitch, AutoUpdateText.Text);
        AutoUpdateSwitch.IsChecked = Updater.ChecksAutomatically;
        VersionText.Text = Updater.CurrentVersion is { } current ? L($"Версия {current}", $"Version {current}") : L("Версия неизвестна", "Unknown version");
        CheckButton.Content = Updater.Phase == UpdatePhase.Checking ? L("Проверка…", "Checking…") : L("Проверить сейчас", "Check now");
        CheckButton.IsEnabled = Updater.Phase == UpdatePhase.Idle && Updater.CurrentVersion is not null;

        // Доступное обновление можно поставить отсюда всегда — и после «Позже», и после «Проверить сейчас».
        var available = Updater.Available;
        UpdateAvailable.Visibility = Shown(available is not null);
        if (available is not null)
        {
            SettingsInstall.Visibility = Shown(!Busy);
            SettingsInstall.Content = L($"Обновить до {available.Version}", $"Update to {available.Version}");
            SettingsInstall.IsEnabled = Updater.Phase == UpdatePhase.Idle;
            SettingsProgress.Visibility = Shown(Busy);
            SettingsProgress.Text = ProgressText;
        }
        // Сообщение — там, где пользователь его ждёт: в полосе, если она видна, иначе здесь.
        UpdateNoticeText.Visibility = Shown(!Updater.ShowsBanner && Updater.Notice is not null);
        UpdateNoticeText.Text = Updater.NoticeText;
        SetNoticeColor(UpdateNoticeText);
    }

    private void SetNoticeColor(TextBlock text) =>
        text.SetResourceReference(TextBlock.ForegroundProperty,
            Updater.Notice is UpdateNotice.UpToDate or null ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");

    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _ = Updater.InstallAsync();

    private void OnDismissUpdate(object sender, RoutedEventArgs e) => Updater.Dismiss();

    private void OnAutoUpdateClick(object sender, RoutedEventArgs e) => Updater.ChecksAutomatically = AutoUpdateSwitch.IsChecked == true;

    private void OnCheckUpdates(object sender, RoutedEventArgs e) => _ = Updater.CheckAsync(manual: true);

    // MARK: - Win32

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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
