using System.Text.Json;

namespace Clipvey.Core;

/// Связанное устройство и последний адрес, по которому с ним удалось соединиться.
/// Enabled = false — связь сохранена, но синхронизация с устройством выключена.
/// Os, Form и Alias появились после 0.1.0: в старых файлах их нет, тогда они null.
/// Name — имя, которое устройство сообщает о себе; Alias — локальный псевдоним, заданный пользователем.
public sealed record StoredDevice(
    string DeviceId,
    string Name,
    string PublicKey,
    string? LastHost,
    int LastPort,
    bool Enabled = true,
    string? Os = null,
    string? Form = null,
    string? Alias = null)
{
    public DeviceType Type => new(Os, Form);

    public PairedDevice ToPaired() => new(DeviceId, Name, Convert.FromBase64String(PublicKey), Type);
}

/// Список связанных устройств в devices.json.
public sealed class DeviceStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory, "devices.json");
    private readonly object _lock = new();

    public List<StoredDevice> Load()
    {
        lock (_lock)
            return LoadUnlocked();
    }

    public void Upsert(StoredDevice device)
    {
        lock (_lock)
        {
            var devices = LoadUnlocked();
            devices.RemoveAll(existing => existing.DeviceId == device.DeviceId);
            devices.Add(device);
            SaveUnlocked(devices);
        }
    }

    public void SetEnabled(string deviceId, bool enabled)
    {
        lock (_lock)
        {
            var devices = LoadUnlocked();
            var index = devices.FindIndex(existing => existing.DeviceId == deviceId);
            if (index < 0 || devices[index].Enabled == enabled)
                return;
            devices[index] = devices[index] with { Enabled = enabled };
            SaveUnlocked(devices);
        }
    }

    public void Remove(string deviceId)
    {
        lock (_lock)
        {
            var devices = LoadUnlocked();
            if (devices.RemoveAll(existing => existing.DeviceId == deviceId) > 0)
                SaveUnlocked(devices);
        }
    }

    /// port = null — оставить прежний (у входящего соединения порт слушателя другой стороны неизвестен).
    public void UpdateEndpoint(string deviceId, string host, int? port) =>
        Update(deviceId, current => current with { LastHost = host, LastPort = port ?? current.LastPort });

    /// Имя и тип из ready или info. null — поле не пришло, оставить прежнее.
    public void UpdateInfo(string deviceId, string? name, DeviceType type) =>
        Update(deviceId, current => current with
        {
            Name = string.IsNullOrEmpty(name) ? current.Name : name,
            Os = type.Os ?? current.Os,
            Form = type.Form ?? current.Form,
        });

    /// alias = null — сбросить псевдоним.
    public void SetAlias(string deviceId, string? alias) =>
        Update(deviceId, current => current with { Alias = alias });

    private void Update(string deviceId, Func<StoredDevice, StoredDevice> change)
    {
        lock (_lock)
        {
            var devices = LoadUnlocked();
            var index = devices.FindIndex(existing => existing.DeviceId == deviceId);
            if (index < 0)
                return;
            var updated = change(devices[index]);
            if (updated == devices[index])
                return;
            devices[index] = updated;
            SaveUnlocked(devices);
        }
    }

    private List<StoredDevice> LoadUnlocked()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<StoredDevice>>(File.ReadAllText(_path)) ?? [];
        }
        catch (JsonException e)
        {
            Log.Write($"Не удалось прочитать {_path}: {e.Message}");
            return [];
        }
    }

    private void SaveUnlocked(List<StoredDevice> devices)
    {
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(devices, Options));
        File.Move(temporary, _path, overwrite: true);
    }
}
