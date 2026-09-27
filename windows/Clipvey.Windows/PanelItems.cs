using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Основа строк списков панели: сообщает WPF об изменённых свойствах.
/// Строки обновляются на месте, а не пересоздаются, — поэтому поле псевдонима не теряет фокус и набранный текст.
internal abstract class PanelItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

/// Состояние связанного устройства для точки у значка.
internal enum DeviceState
{
    /// Не в сети или синхронизация выключена: серая точка.
    Idle,

    /// Подключено: зелёная точка.
    Connected,

    /// Не удалось подключиться по известной причине: жёлтая точка и статус того же цвета.
    Problem,
}

/// Карточка связанного устройства.
internal sealed class DeviceItem(string id) : PanelItem
{
    public string Id { get; } = id;
    public DeviceStatus Device { get; private set; } = null!;

    private string _glyph = "", _typeDescription = "", _title = "", _realName = "", _status = "";
    private DeviceState _state;
    private bool _enabled;
    private Visibility _realNameVisibility, _renameVisibility, _resetVisibility, _unpairVisibility;
    private string _toggleName = "", _moreName = "", _aliasPrompt = "", _aliasDraft = "";
    private string _saveText = "", _resetText = "", _cancelText = "", _unpairQuestion = "", _unpairText = "";

    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public string TypeDescription { get => _typeDescription; private set => Set(ref _typeDescription, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string RealName { get => _realName; private set => Set(ref _realName, value); }
    public Visibility RealNameVisibility { get => _realNameVisibility; private set => Set(ref _realNameVisibility, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public DeviceState State { get => _state; private set => Set(ref _state, value); }
    public bool Enabled { get => _enabled; private set => Set(ref _enabled, value); }
    public string ToggleName { get => _toggleName; private set => Set(ref _toggleName, value); }
    public string MoreName { get => _moreName; private set => Set(ref _moreName, value); }

    public Visibility RenameVisibility { get => _renameVisibility; private set => Set(ref _renameVisibility, value); }
    public string AliasPrompt { get => _aliasPrompt; private set => Set(ref _aliasPrompt, value); }

    /// Набранный псевдоним. Меняет его только пользователь (и начало переименования).
    public string AliasDraft { get => _aliasDraft; set => Set(ref _aliasDraft, value); }

    public string SaveText { get => _saveText; private set => Set(ref _saveText, value); }
    public string ResetText { get => _resetText; private set => Set(ref _resetText, value); }
    public Visibility ResetVisibility { get => _resetVisibility; private set => Set(ref _resetVisibility, value); }
    public string CancelText { get => _cancelText; private set => Set(ref _cancelText, value); }

    public Visibility UnpairVisibility { get => _unpairVisibility; private set => Set(ref _unpairVisibility, value); }
    public string UnpairQuestion { get => _unpairQuestion; private set => Set(ref _unpairQuestion, value); }
    public string UnpairText { get => _unpairText; private set => Set(ref _unpairText, value); }

    // Имена для UI Automation (проверки и экранный диктор).
    public string ToggleId => "toggle:" + Id;
    public string MoreId => "more:" + Id;
    public string AliasId => "alias:" + Id;
    public string SaveId => "alias-save:" + Id;
    public string ResetId => "alias-reset:" + Id;
    public string CancelId => "alias-cancel:" + Id;
    public string UnpairYesId => "unpair-yes:" + Id;
    public string UnpairNoId => "unpair-no:" + Id;

    public void Update(DeviceStatus device, bool imagesEnabled, bool renaming, bool confirmingUnpair)
    {
        Device = device;
        Glyph = UiText.Glyph(device.Type);
        TypeDescription = UiText.Describe(device.Type);
        Title = device.DisplayName;
        // Псевдоним задан — настоящее имя устройства видно второй строкой.
        RealName = device.Alias is null ? "" : L($"Имя на устройстве: {device.Name}", $"Device name: {device.Name}");
        RealNameVisibility = Shown(device.Alias is not null);

        var status = !device.Enabled ? L("синхронизация выключена", "sync is off")
            : device.Connected ? L("подключено", "connected")
            : device.Problem is { } problem ? UiText.Failure(problem) : L("не в сети", "offline");
        // Картинки этому устройству не уходят: у него они выключены или старая версия Clipvey.
        if (device.Connected && device.Enabled && imagesEnabled && !device.AcceptsImages)
            status += L(" · только текст", " · text only");
        Status = status;
        State = device.Connected ? DeviceState.Connected
            : device.Enabled && device.Problem is not null ? DeviceState.Problem
            : DeviceState.Idle;
        Enabled = device.Enabled;
        ToggleName = L($"Синхронизация с «{device.DisplayName}»", $"Sync with “{device.DisplayName}”");
        MoreName = L($"Действия с «{device.DisplayName}»", $"Actions for “{device.DisplayName}”");

        RenameVisibility = Shown(renaming);
        AliasPrompt = L("Имя на этом компьютере (другие устройства его не видят):", "Name on this PC (other devices don’t see it):");
        SaveText = L("Сохранить", "Save");
        ResetText = L("Сбросить", "Reset");
        ResetVisibility = Shown(device.Alias is not null);
        CancelText = L("Отмена", "Cancel");

        UnpairVisibility = Shown(confirmingUnpair);
        UnpairQuestion = L($"Разорвать связь с «{device.DisplayName}»? Чтобы снова синхронизироваться, их придётся связать заново.",
            $"Unpair “{device.DisplayName}”? You’ll need to pair again to sync.");
        UnpairText = L("Разорвать связь", "Unpair");
    }
}

/// Устройство в режиме связывания, с которым ещё нет связи.
internal sealed class CandidateItem(string key) : PanelItem
{
    public string Key { get; } = key;
    public DiscoveredDevice Device { get; private set; } = null!;

    private string _glyph = "", _typeDescription = "", _name = "", _pairText = "";

    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public string TypeDescription { get => _typeDescription; private set => Set(ref _typeDescription, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string PairText { get => _pairText; private set => Set(ref _pairText, value); }
    public string PairId => "candidate:" + Key;

    public static string KeyOf(DiscoveredDevice device) => device.DeviceId ?? device.Name;

    public void Update(DiscoveredDevice device)
    {
        Device = device;
        Glyph = UiText.Glyph(device.Type);
        TypeDescription = UiText.Describe(device.Type);
        Name = device.Name;
        PairText = L("Связать", "Pair");
    }
}
