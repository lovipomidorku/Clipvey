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

    /// Сколько последних id фрагментов помнить для отбрасывания повторов.
    public const int SeenClipsCapacity = 500;
}

public class ProtocolException(string message) : Exception(message);

/// Другое устройство прислало error или pair_abort.
public sealed class PeerRejectedException(string reason) : ProtocolException(Describe(reason))
{
    public string Reason { get; } = reason;

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

public static class Log
{
    /// Куда писать журнал (консоль, файл). Реализация должна быть потокобезопасной.
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) =>
        Sink?.Invoke($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
}
