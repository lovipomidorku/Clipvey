namespace Clipvey.Core;

/// Константы протокола. Описание — docs/protocol.md.
public static class Protocol
{
    public const int Version = 1;
    public const int DefaultPort = 48620;
    public const string ServiceType = "_clipvey._tcp";
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public const int MaxClipBytes = 1024 * 1024;

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);

    /// Сколько ждёт устройство с большим deviceId, прежде чем подключиться само.
    public static readonly TimeSpan SecondaryConnectDelay = TimeSpan.FromSeconds(5);

    /// Пауза между попытками подключиться к устройству, на котором синхронизация с нами выключена.
    public static readonly TimeSpan DisabledRetryDelay = TimeSpan.FromSeconds(30);

    /// Дальше этого числа пересылок фрагмент не передаётся.
    public const int MaxHops = 8;

    /// Сколько последних id фрагментов (общих для текста и картинок) помнить для отбрасывания повторов.
    public const int SeenClipsCapacity = 500;

    /// Картинка — от 1 байта до 20 МиБ.
    public const int MaxImageBytes = 20_971_520;

    /// Кусок картинки до кодирования: все, кроме последнего, ровно 512 КиБ.
    public const int BlobChunkBytes = 524_288;

    /// Возможность в caps: устройство принимает картинки.
    public const string ImageCapability = "image";

    /// Допустимые mime картинок.
    public static readonly IReadOnlySet<string> ImageMimes = new HashSet<string> { "image/png", "image/jpeg" };
}

/// Почему не удалось связаться или подключиться. Код для интерфейса: текст на нужном языке
/// подбирает интерфейс, узел отдаёт только код. Журнал пишется по-русски (Exception.Message).
/// Имена совпадают с FailureReason.code на Mac.
public enum FailureReason
{
    /// На другом устройстве не открыт режим связывания (error not_pairing).
    NotPairing,
    /// Другое устройство уже связывается с кем-то (error busy).
    Busy,
    /// На другом устройстве введён неверный код (pair_abort code).
    PeerCodeMismatch,
    /// Связывание отменено на другом устройстве (pair_abort cancel).
    PeerCancelled,
    /// Обязательство не совпало — возможно, соединение перехвачено.
    CommitMismatch,
    /// Другое устройство не знает это — нужно связать заново (error unknown_device).
    UnknownDevice,
    /// На другом устройстве синхронизация с этим выключена (error disabled).
    Disabled,
    /// Другое устройство отказало по незнакомой причине.
    Rejected,
    /// Введённый здесь код не совпал.
    CodeMismatch,
    /// Связывание отменено здесь или истекло время.
    Cancelled,
    /// По адресу ответило не то устройство.
    WrongDevice,
    /// Нет соединения.
    ConnectionFailed,
    /// Нарушение протокола.
    ProtocolError,
}

public static class Failures
{
    /// Код по причине из error / pair_abort.
    public static FailureReason FromPeerReason(string reason) => reason switch
    {
        "not_pairing" => FailureReason.NotPairing,
        "busy" => FailureReason.Busy,
        "code" => FailureReason.PeerCodeMismatch,
        "cancel" => FailureReason.PeerCancelled,
        "commit" => FailureReason.CommitMismatch,
        "unknown_device" => FailureReason.UnknownDevice,
        "disabled" => FailureReason.Disabled,
        _ => FailureReason.Rejected,
    };

    /// Код для любой ошибки.
    public static FailureReason Of(Exception e) => e switch
    {
        PeerRejectedException rejected => rejected.Failure,
        PairingCodeMismatchException => FailureReason.CodeMismatch,
        WrongDeviceException => FailureReason.WrongDevice,
        CommitMismatchException => FailureReason.CommitMismatch,
        OperationCanceledException => FailureReason.Cancelled,
        ProtocolException => FailureReason.ProtocolError,
        _ => FailureReason.ConnectionFailed,
    };
}

public class ProtocolException(string message) : Exception(message);

/// Другое устройство прислало error или pair_abort.
public sealed class PeerRejectedException(string reason) : ProtocolException(Describe(reason))
{
    public string Reason { get; } = reason;

    public FailureReason Failure => Failures.FromPeerReason(Reason);

    private static string Describe(string reason) => reason switch
    {
        "not_pairing" => "На другом устройстве не открыт режим связывания",
        "busy" => "Другое устройство уже связывается с кем-то",
        "code" => "На другом устройстве введён неверный код",
        "cancel" => "Связывание отменено на другом устройстве",
        "commit" => "Проверка связывания не прошла",
        "unknown_device" => "Другое устройство не знает это — свяжите заново",
        "disabled" => "На другом устройстве синхронизация с этим выключена",
        _ => $"Другое устройство отказало: {reason}",
    };
}

public sealed class PairingCodeMismatchException() : ProtocolException("Код не совпал");

/// По адресу ответило не то устройство, с которым было связывание.
public sealed class WrongDeviceException() : ProtocolException("Ответило не то устройство, с которым было связывание");

/// Обязательство связывания не совпало.
public sealed class CommitMismatchException() : ProtocolException("Другое устройство нарушило обязательство — возможно, соединение перехвачено");

public static class Log
{
    /// Куда писать журнал (консоль, файл). Реализация должна быть потокобезопасной.
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) =>
        Sink?.Invoke($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
}
