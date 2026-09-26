using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Тип устройства: необязательные поля os и form (docs/protocol.md, «Тип устройства»).
/// null — поле не пришло; незнакомое значение хранится как есть, интерфейс показывает общий значок.
public sealed record DeviceType(string? Os, string? Form)
{
    public static readonly DeviceType Unknown = new(null, null);

    /// os и form из pair_hello, pair_commit, ready или info. Не строка — как будто поля нет.
    public static DeviceType Parse(JsonObject message) =>
        new(Messages.OptionalString(message, "os"), Messages.OptionalString(message, "form"));

    internal void WriteTo(JsonObject message)
    {
        if (Os is not null)
            message["os"] = Os;
        if (Form is not null)
            message["form"] = Form;
    }
}

/// Содержимое ready и info. В ready отсутствие caps значит «ничего», в info отсутствие поля — «не изменилось».
public sealed record PeerInfo(string? Name, DeviceType Type, IReadOnlyList<string>? Caps)
{
    public static PeerInfo Parse(JsonObject message)
    {
        List<string>? caps = null;
        if (message["caps"] is JsonArray array)
        {
            caps = [];
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue(out string? cap) && cap is not null)
                    caps.Add(cap);
            }
        }
        return new PeerInfo(Messages.OptionalString(message, "name"), DeviceType.Parse(message), caps);
    }

    /// Сообщение ready или info.
    public JsonObject ToMessage(string type)
    {
        var message = new JsonObject { ["t"] = type };
        if (Name is not null)
            message["name"] = Name;
        Type.WriteTo(message);
        if (Caps is not null)
            message["caps"] = new JsonArray([.. Caps.Select(cap => (JsonNode?)JsonValue.Create(cap))]);
        return message;
    }

    public bool AcceptsImages => Caps?.Contains(Protocol.ImageCapability) ?? false;
}

/// Начало картинки: blob_start. Неверные kind, mime, size и sha256 не рвут сеанс: картинку отвергает BlobAssembly.Refusal.
public sealed record BlobStart(string Id, string Origin, int Hops, string Kind, string Mime, long Size, byte[] Sha256)
{
    public static BlobStart Parse(JsonObject message)
    {
        byte[] sha256;
        try
        {
            sha256 = Convert.FromBase64String(Messages.OptionalString(message, "sha256") ?? "");
        }
        catch (FormatException)
        {
            sha256 = [];
        }
        return new BlobStart(
            Messages.String(message, "id"),
            Messages.String(message, "origin"),
            (int)(Messages.OptionalInteger(message, "hops") ?? 0),
            Messages.OptionalString(message, "kind") ?? "",
            Messages.OptionalString(message, "mime") ?? "",
            Messages.OptionalInteger(message, "size") ?? 0,
            sha256);
    }

    public JsonObject ToMessage() => new()
    {
        ["t"] = "blob_start",
        ["id"] = Id,
        ["origin"] = Origin,
        ["hops"] = Hops,
        ["kind"] = Kind,
        ["mime"] = Mime,
        ["size"] = Size,
        ["sha256"] = Convert.ToBase64String(Sha256),
    };
}

/// Картинки по docs/protocol.md, «Картинки»: нарезка на куски и сообщения кусков. Совпадает с Blob.swift.
public static class Blob
{
    /// Куски: все, кроме последнего, ровно BlobChunkBytes. Без копирования данных.
    public static IEnumerable<ReadOnlyMemory<byte>> Chunks(ReadOnlyMemory<byte> data)
    {
        for (var start = 0; start < data.Length; start += Protocol.BlobChunkBytes)
            yield return data.Slice(start, Math.Min(Protocol.BlobChunkBytes, data.Length - start));
    }

    public static JsonObject ChunkMessage(string id, int seq, ReadOnlySpan<byte> data) => new()
    {
        ["t"] = "blob_chunk",
        ["id"] = id,
        ["seq"] = seq,
        ["data"] = Convert.ToBase64String(data),
    };

    public static JsonObject EndMessage(string id) => new() { ["t"] = "blob_end", ["id"] = id };

    /// Данные куска; null — поля нет или оно не base64 (картинка отбрасывается, сеанс продолжается).
    public static byte[]? ChunkData(JsonObject message)
    {
        try
        {
            return Messages.OptionalString(message, "data") is { } text ? Convert.FromBase64String(text) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// Новый id фрагмента: 16 случайных байт в hex (то же пространство, что у clip).
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// Сборка одной картинки из blob_chunk. Ошибка любой проверки — картинка отбрасывается (сеанс продолжается).
public sealed class BlobAssembly(BlobStart header)
{
    private readonly byte[] _data = new byte[header.Size];
    private int _received;
    private int _nextSeq;

    public BlobStart Header { get; } = header;

    /// Почему картинку нельзя принять (для журнала); null — можно. «Передавать картинки» и повторы проверяет узел.
    public static string? Refusal(BlobStart start)
    {
        if (start.Kind != "image")
            return $"незнакомый вид «{start.Kind}»";
        if (!Protocol.ImageMimes.Contains(start.Mime))
            return $"незнакомый mime «{start.Mime}»";
        if (start.Size is < 1 or > Protocol.MaxImageBytes)
            return $"размер {start.Size} байт вне допустимого";
        if (start.Sha256.Length != 32)
            return "sha256 не 32 байта";
        return null;
    }

    /// Принять кусок. Бросает ProtocolException, если seq не следующий, данных нет или кусок не той длины.
    public void Append(int seq, byte[]? chunk)
    {
        if (seq != _nextSeq)
            throw new ProtocolException($"кусок {seq} вместо {_nextSeq}");
        if (chunk is null || chunk.Length == 0)
            throw new ProtocolException($"кусок {seq} без данных");
        var total = (long)_received + chunk.Length;
        if (total > Header.Size)
            throw new ProtocolException($"данных больше заявленных {Header.Size} байт");
        // Кусок, после которого данных ещё не хватает, — не последний: ровно 512 КиБ.
        if (total != Header.Size && chunk.Length != Protocol.BlobChunkBytes)
            throw new ProtocolException($"кусок {seq} длиной {chunk.Length} байт вместо {Protocol.BlobChunkBytes}");
        chunk.CopyTo(_data, _received);
        _received = (int)total;
        _nextSeq++;
    }

    /// blob_end: вернуть данные, если хватает и совпал sha256.
    public byte[] Finish()
    {
        if (_received != Header.Size)
            throw new ProtocolException($"получено {_received} байт из {Header.Size}");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(_data), Header.Sha256))
            throw new ProtocolException("sha256 не совпал");
        return _data;
    }
}
