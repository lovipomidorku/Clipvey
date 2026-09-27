using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Тексты и значки интерфейса, которые нужны и панели, и значку в трее.
internal static class UiText
{
    /// Причина неудачи на языке интерфейса (узел отдаёт только код).
    public static string Failure(FailureReason reason) => reason switch
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

    /// «Последняя синхронизация: 11:03 · текст от OFFICE-PC» / «… 11:05 · картинка отправлена».
    public static string LastSync(LastSync? sync)
    {
        if (sync is null)
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

    /// Глиф значка типа устройства (Segoe Fluent Icons / Segoe MDL2 Assets, коды есть в обоих шрифтах):
    /// - E7F8 DeviceLaptopNoPic — ноутбук (Mac или ПК);
    /// - E977 PC1 — настольный ПК с Windows;
    /// - E7FB DeviceMonitorNoPic — настольный Mac (iMac, Mac mini, Mac Studio);
    /// - E772 Devices — тип неизвестен (старая версия Clipvey или незнакомые os/form).
    public static string Glyph(DeviceType? type) => (type?.Os, type?.Form) switch
    {
        (_, "laptop") => "",
        ("mac", _) => "",
        ("windows", _) => "",
        (_, "desktop") => "",
        _ => "",
    };

    /// Для экранного диктора: «Mac, ноутбук», «ПК с Windows» и т. п.
    public static string Describe(DeviceType? type)
    {
        var os = type?.Os switch
        {
            "mac" => "Mac",
            "windows" => L("ПК с Windows", "Windows PC"),
            _ => L("Устройство", "Device"),
        };
        return type?.Form switch
        {
            "laptop" => L($"{os}, ноутбук", $"{os}, laptop"),
            "desktop" => L($"{os}, настольный", $"{os}, desktop"),
            _ => os,
        };
    }
}
