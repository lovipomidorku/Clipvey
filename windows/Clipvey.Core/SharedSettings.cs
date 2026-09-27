using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Общие настройки (docs/protocol.md, «Общие настройки»): меняются на одном устройстве — применяются на всех.
/// AutoDownloadMB — до какого размера (МиБ) полученные файлы скачиваются тихо, в фоне; больше — по действию пользователя.
/// Changed — когда настройки изменил пользователь (мс с 1970-01-01 UTC, 0 — не менял), By — deviceId устройства,
/// на котором их изменили. Совпадает с SharedSettings на Mac.
public sealed record SharedSettings(int AutoDownloadMB, long Changed, string By)
{
    /// Допустимые значения AutoDownloadMB по возрастанию. 10240 — «всегда»: весь допустимый размер описания (10 ГиБ).
    public static IReadOnlyList<int> AllowedMB { get; } = [50, 100, 300, 500, 1000, 10240];

    public const int DefaultMB = 50;

    /// «Всегда»: тихо скачивается всё, что вообще можно передать.
    public const int AlwaysMB = 10240;

    /// Пока пользователь ничего не менял: 50 МиБ, changed = 0, by = свой deviceId.
    public static SharedSettings Default(string deviceId) => new(DefaultMB, 0, deviceId);

    /// Ближайшее допустимое значение; при равном расстоянии — меньшее. Отрицательное и 0 — 50.
    public static int Nearest(int mb) => Nearest((double)mb);

    /// То же для любого числа из сообщения (в том числе дробного и огромного); не число — 50.
    public static int Nearest(double mb)
    {
        if (double.IsNaN(mb))
            return DefaultMB;
        var best = AllowedMB[0];
        foreach (var allowed in AllowedMB)
        {
            if (Math.Abs(mb - allowed) < Math.Abs(mb - best))
                best = allowed;
        }
        return best;
    }

    /// Порог в байтах: полученные файлы всего не больше него скачиваются тихо.
    public long AutoDownloadBytes => AutoDownloadMB * 1_048_576L;

    /// Эти настройки новее other: большее Changed, при равном — большее By (порядковое сравнение строк).
    public bool IsNewerThan(SharedSettings other) =>
        Changed != other.Changed ? Changed > other.Changed : string.CompareOrdinal(By, other.By) > 0;

    /// Значение приведено к допустимому (Nearest), By не null.
    public SharedSettings Normalized(string ownDeviceId) =>
        this with { AutoDownloadMB = Nearest(AutoDownloadMB), Changed = Math.Max(0, Changed), By = By ?? ownDeviceId };

    public JsonObject ToMessage() => new()
    {
        ["t"] = "settings",
        ["autoDownloadMB"] = AutoDownloadMB,
        ["changed"] = Changed,
        ["by"] = By,
    };

    /// Разбор settings. Поля не обязательны (старые и будущие версии): autoDownloadMB — любое число, приводится
    /// к ближайшему допустимому, нет или не число — 50; changed — целое от 0 до 9·10^15, иначе 0; by — строка, иначе "".
    /// Настройки без changed и by никогда не новее своих: так пустое сообщение ничего не меняет.
    public static SharedSettings Parse(JsonObject message)
    {
        var mb = DefaultMB;
        if (Messages.OptionalInteger(message, "autoDownloadMB") is { } integer)
            mb = Nearest((double)integer);
        else if (message["autoDownloadMB"] is JsonValue value && value.TryGetValue(out double number))
            mb = Nearest(number);
        // Как на Mac: целые до 9·10^15 (точно представимые в double).
        var changed = Messages.OptionalInteger(message, "changed") is { } time and > 0 and < 9_000_000_000_000_000 ? time : 0;
        return new SharedSettings(mb, changed, Messages.OptionalString(message, "by") ?? "");
    }

    /// Для журнала и событий самопроверки: «300 1790000000000 <by>».
    public override string ToString() => $"{AutoDownloadMB} {Changed} {By}";
}
