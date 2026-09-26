using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Кадры: u32 длина (big-endian) + нагрузка.
public static class Frames
{
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length is 0 or > Protocol.MaxFrameBytes)
            throw new ProtocolException($"Недопустимая длина кадра: {payload.Length}");
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)payload.Length);
        payload.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > Protocol.MaxFrameBytes)
            throw new ProtocolException($"Недопустимая длина кадра: {length}");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }
}

/// Сообщения протокола — объекты JSON с полем "t".
public static class Messages
{
    // Без экранирования кириллицы: текст буфера передаётся как есть в UTF-8.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Encode(JsonObject message) => Encoding.UTF8.GetBytes(message.ToJsonString(Options));

    public static JsonObject Decode(byte[] payload)
    {
        try
        {
            return JsonNode.Parse(payload) as JsonObject ?? throw new ProtocolException("Сообщение — не объект JSON");
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"Неверный JSON: {e.Message}");
        }
    }

    public static string Type(JsonObject message) => String(message, "t");

    public static string String(JsonObject message, string key)
    {
        try
        {
            return message[key]?.GetValue<string>() ?? throw new ProtocolException($"Нет поля «{key}»");
        }
        catch (InvalidOperationException)
        {
            throw new ProtocolException($"Поле «{key}» — не строка");
        }
    }

    public static byte[] Bytes(JsonObject message, string key, int expectedLength)
    {
        byte[] value;
        try
        {
            value = Convert.FromBase64String(String(message, key));
        }
        catch (FormatException)
        {
            throw new ProtocolException($"Поле «{key}» — не base64");
        }
        if (value.Length != expectedLength)
            throw new ProtocolException($"Поле «{key}»: ожидалось {expectedLength} байт, получено {value.Length}");
        return value;
    }

    /// Проверяет тип сообщения. На error и pair_abort бросает PeerRejectedException.
    public static JsonObject Expect(JsonObject message, string type)
    {
        var actual = Type(message);
        if (actual == type)
            return message;
        if (actual is "error" or "pair_abort")
            throw new PeerRejectedException(message["reason"]?.GetValue<string>() ?? actual);
        throw new ProtocolException($"Ожидалось «{type}», получено «{actual}»");
    }
}

/// Шифрованный канал сеанса: AES-256-GCM, свой ключ и счётчик для каждого направления.
public sealed class SecureChannel : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly AesGcm _sendCipher;
    private readonly AesGcm _receiveCipher;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ulong _sendCounter;
    private ulong _receiveCounter;

    internal SecureChannel(TcpClient tcp, NetworkStream stream, byte[] sendKey, byte[] receiveKey)
    {
        _tcp = tcp;
        _stream = stream;
        _sendCipher = new AesGcm(sendKey, 16);
        _receiveCipher = new AesGcm(receiveKey, 16);
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var plaintext = Messages.Encode(message);
        await _sendLock.WaitAsync(ct);
        try
        {
            var payload = new byte[plaintext.Length + 16];
            _sendCipher.Encrypt(Nonce(_sendCounter++), plaintext, payload.AsSpan(0, plaintext.Length), payload.AsSpan(plaintext.Length));
            await Frames.WriteAsync(_stream, payload, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// Вызывать из одного потока чтения.
    public async Task<JsonObject> ReceiveAsync(CancellationToken ct)
    {
        var frame = await Frames.ReadAsync(_stream, ct);
        if (frame.Length < 16)
            throw new ProtocolException("Слишком короткий шифрованный кадр");
        var plaintext = new byte[frame.Length - 16];
        try
        {
            _receiveCipher.Decrypt(Nonce(_receiveCounter++), frame.AsSpan(0, plaintext.Length), frame.AsSpan(plaintext.Length), plaintext);
        }
        catch (CryptographicException)
        {
            throw new ProtocolException("Не удалось расшифровать кадр");
        }
        return Messages.Decode(plaintext);
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
        _sendCipher.Dispose();
        _receiveCipher.Dispose();
        _sendLock.Dispose();
    }
}
