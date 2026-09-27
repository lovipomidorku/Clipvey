using System.Buffers;
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

    public static JsonObject Decode(ReadOnlySpan<byte> payload)
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

    /// Необязательная строка: нет поля или не строка — null.
    public static string? OptionalString(JsonObject message, string key) =>
        message[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// Необязательное целое: нет поля, не число или дробное — null.
    public static long? OptionalInteger(JsonObject message, string key)
    {
        if (message[key] is not JsonValue value)
            return null;
        if (value.TryGetValue(out long number))
            return number;
        if (value.TryGetValue(out int small))
            return small;
        return null;
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
/// Кадры с кусками файлов (до 1 МиБ) идут через буферы ArrayPool и шифруются на месте, без лишних копий.
public sealed class SecureChannel : IAsyncDisposable
{
    private const int TagBytes = 16;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly AesGcm _sendCipher;
    private readonly AesGcm _receiveCipher;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveHeader = new byte[4];
    private ulong _sendCounter;
    private ulong _receiveCounter;

    internal SecureChannel(TcpClient tcp, NetworkStream stream, byte[] sendKey, byte[] receiveKey)
    {
        _tcp = tcp;
        _stream = stream;
        _sendCipher = new AesGcm(sendKey, TagBytes);
        _receiveCipher = new AesGcm(receiveKey, TagBytes);
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var plaintext = Messages.Encode(message);
        await _sendLock.WaitAsync(ct);
        try
        {
            var payload = new byte[plaintext.Length + TagBytes];
            _sendCipher.Encrypt(Nonce(_sendCounter++), plaintext, payload.AsSpan(0, plaintext.Length), payload.AsSpan(plaintext.Length));
            await Frames.WriteAsync(_stream, payload, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// Двоичный кусок файла: `00 ‖ req ‖ данные` (данные копируются в буфер кадра; без копии — SendChunkAsync(FileChunkFrame…)).
    public async Task SendChunkAsync(uint req, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var frame = FileChunkFrame.Rent();
        data.Span.CopyTo(frame.Data.Span);
        await SendChunkAsync(frame, req, data.Length, ct);
    }

    /// Двоичный кусок из буфера кадра, в Data которого уже лежат length байт: заголовок дописывается,
    /// открытый текст шифруется на месте, кадр уходит одной записью.
    public async Task SendChunkAsync(FileChunkFrame frame, uint req, int length, CancellationToken ct)
    {
        if (length is < 1 or > Protocol.FileChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = frame.Buffer;
        var plaintextLength = FileChunk.HeaderBytes + length;
        var total = 4 + plaintextLength + TagBytes;
        FileChunk.WriteHeader(buffer.AsSpan(4), req);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(plaintextLength + TagBytes));
        await _sendLock.WaitAsync(ct);
        try
        {
            var plaintext = buffer.AsSpan(4, plaintextLength);
            _sendCipher.Encrypt(Nonce(_sendCounter++), plaintext, plaintext, buffer.AsSpan(4 + plaintextLength, TagBytes));
            await _stream.WriteAsync(buffer.AsMemory(0, total), ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// Следующее сообщение JSON (рукопожатие, проверки). Двоичный кадр здесь — нарушение протокола.
    public async Task<JsonObject> ReceiveAsync(CancellationToken ct)
    {
        using var frame = await ReceiveFrameAsync(ct);
        return frame.Message ?? throw new ProtocolException("Ожидалось сообщение JSON, получен двоичный кадр");
    }

    /// Следующий кадр: сообщение JSON или двоичный кусок файла. Вызывать из одного потока чтения;
    /// кадр с куском держит буфер из пула — его нужно освободить (Dispose) или забрать (TakeChunk).
    public async Task<SessionFrame> ReceiveFrameAsync(CancellationToken ct)
    {
        await _stream.ReadExactlyAsync(_receiveHeader, ct);
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(_receiveHeader), int.MaxValue);
        if (length is 0 or > Protocol.MaxFrameBytes)
            throw new ProtocolException($"Недопустимая длина кадра: {length}");
        if (length < TagBytes)
            throw new ProtocolException("Слишком короткий шифрованный кадр");
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await _stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
            var plaintextLength = length - TagBytes;
            var plaintext = buffer.AsSpan(0, plaintextLength);
            try
            {
                _receiveCipher.Decrypt(Nonce(_receiveCounter++), plaintext, buffer.AsSpan(plaintextLength, TagBytes), plaintext);
            }
            catch (CryptographicException)
            {
                throw new ProtocolException("Не удалось расшифровать кадр");
            }
            if (plaintextLength > 0 && plaintext[0] == 0)
            {
                try
                {
                    var (req, dataLength) = FileChunk.Parse(plaintext);
                    var chunk = new FileChunkData(buffer, FileChunk.HeaderBytes, dataLength);
                    buffer = null;
                    return new SessionFrame(req, chunk);
                }
                catch (FileMessageException e)
                {
                    return new SessionFrame(e.Message);
                }
            }
            return new SessionFrame(Messages.Decode(plaintext));
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// Для проверок (Clipvey.Tests, tests/vectors.json): начать с заданных счётчиков.
    internal void SetCounters(ulong send, ulong receive)
    {
        _sendCounter = send;
        _receiveCounter = receive;
    }

    internal static byte[] Nonce(ulong counter)
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

/// Кадр сеанса: сообщение JSON, двоичный кусок файла или двоичный кадр, не прошедший проверку (Invalid —
/// причина для журнала; кадр пропускается, сеанс продолжается).
public sealed class SessionFrame : IDisposable
{
    private FileChunkData? _chunk;

    internal SessionFrame(JsonObject message) => Message = message;

    internal SessionFrame(uint req, FileChunkData chunk)
    {
        Req = req;
        _chunk = chunk;
    }

    internal SessionFrame(string invalid) => Invalid = invalid;

    public JsonObject? Message { get; }

    public string? Invalid { get; }

    /// req куска (для кусков).
    public uint Req { get; }

    public bool IsChunk => Message is null && Invalid is null;

    /// Данные куска (пока кадр не освобождён и кусок не забран).
    public ReadOnlyMemory<byte> ChunkData => _chunk?.Data ?? ReadOnlyMemory<byte>.Empty;

    /// Забрать кусок вместе с буфером: освобождать его будет новый владелец.
    public FileChunkData TakeChunk()
    {
        var chunk = _chunk ?? throw new InvalidOperationException("Кадр — не кусок файла или кусок уже забран");
        _chunk = null;
        return chunk;
    }

    public void Dispose()
    {
        _chunk?.Dispose();
        _chunk = null;
    }
}

/// Данные куска файла в буфере из ArrayPool. Dispose возвращает буфер в пул.
public sealed class FileChunkData : IDisposable
{
    private byte[]? _buffer;
    private readonly int _offset;

    internal FileChunkData(byte[] buffer, int offset, int length)
    {
        _buffer = buffer;
        _offset = offset;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Data => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(_offset, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// Буфер кадра с куском файла из ArrayPool: данные читаются прямо в Data, затем SecureChannel.SendChunkAsync
/// дописывает заголовки и шифрует на месте. Dispose возвращает буфер в пул.
public sealed class FileChunkFrame : IDisposable
{
    /// Длина кадра (4 байта) и заголовок куска (5 байт) перед данными.
    private const int DataOffset = 4 + FileChunk.HeaderBytes;

    private byte[]? _buffer;

    private FileChunkFrame(byte[] buffer) => _buffer = buffer;

    public static FileChunkFrame Rent() => new(ArrayPool<byte>.Shared.Rent(DataOffset + Protocol.FileChunkBytes + 16));

    internal byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(FileChunkFrame));

    /// Место под данные куска: до 1 МиБ.
    public Memory<byte> Data => Buffer.AsMemory(DataOffset, Protocol.FileChunkBytes);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
