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

    /// Почему файлы не отправлены — коротко, для окошка.
    public static string OfferFailure(FileOfferFailure failure) => failure switch
    {
        FileOfferFailure.Empty => L("Нечего отправлять: символические ссылки, точки соединения и особые файлы не передаются",
            "Nothing to send: symbolic links, junctions and special files aren’t shared"),
        FileOfferFailure.TooManyItems => L("Больше 10 000 файлов и папок", "More than 10,000 files and folders"),
        FileOfferFailure.TooLarge => L("Всего больше 10 ГБ", "More than 10 GB in total"),
        FileOfferFailure.NameTooLong => L("Слишком длинное имя или путь", "A name or path is too long"),
        FileOfferFailure.Disabled => L("Передача файлов выключена", "File sharing is off"),
        _ => L("Не удалось прочитать файлы", "Couldn’t read the files"),
    };

    /// Почему файлы не получены — коротко, для окошка. from — имя устройства-источника.
    public static string TransferFailure(FileTransferFailure failure, string from) => failure switch
    {
        FileTransferFailure.DeviceUnavailable => L($"«{from}» недоступно — связь прервалась", $"“{from}” is unavailable — the connection was lost"),
        FileTransferFailure.NotFound => L($"На «{from}» этих файлов уже нет — скопируйте их снова",
            $"These files are no longer available on “{from}”. Copy them again"),
        FileTransferFailure.Changed => L($"Файл на «{from}» изменился после копирования — скопируйте снова",
            $"A file on “{from}” changed after it was copied. Copy it again"),
        FileTransferFailure.Unavailable => L($"«{from}» не смогло прочитать файл", $"“{from}” couldn’t read a file"),
        FileTransferFailure.WriteFailed => L("Не удалось записать файл на диск", "Couldn’t write a file to disk"),
        FileTransferFailure.ProtocolError => L("Файлы пришли с ошибкой", "The files arrived damaged"),
        FileTransferFailure.Cancelled => L("Отменено", "Cancelled"),
        _ => L("Файлы не получены", "The files weren’t received"),
    };

    /// «12,3 МБ» — размер для окошка. Как в Проводнике: 1 КБ = 1024 байта.
    public static string Size(long bytes)
    {
        string[] units = [L("Б", "B"), L("КБ", "KB"), L("МБ", "MB"), L("ГБ", "GB")];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : "0.0";
        return $"{value.ToString(format, System.Globalization.CultureInfo.CurrentCulture)} {units[unit]}";
    }

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
            (SyncKind.Files, { } from) => L($"файлы от {from}", $"files from {from}"),
            (SyncKind.Files, null) => L("файлы отправлены", "files sent"),
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
