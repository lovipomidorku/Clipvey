using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Clipvey.Core;
using static Clipvey.Tests.Vectors;

namespace Clipvey.Tests;

/// Файлы (docs/protocol.md, «Файлы»): сообщения, правила описания, двоичные куски, имена, обход папок —
/// те же образцы tests/vectors.json, что проверяет Mac.
public class FileProtocolTests
{
    private const string OfferId = "0123456789abcdef0123456789abcdef";

    private static JsonObject SampleMessage(string name) =>
        JsonNode.Parse(String(Root["messages"]!.AsArray().Single(message => String(message!["name"]) == name)!["json"]))!.AsObject();

    private static JsonObject Parse(string json) => Messages.Decode(Encoding.UTF8.GetBytes(json));

    private static void AssertSameObject(JsonObject expected, JsonObject actual) =>
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(Messages.Encode(actual))), Encoding.UTF8.GetString(Messages.Encode(actual)));

    [Fact]
    public void OfferSample()
    {
        var sample = SampleMessage("file_offer");
        var offer = FileOffer.Parse(sample);
        Assert.Equal(OfferId, offer.Id);
        Assert.Equal(5_000_012_345, offer.Total);
        Assert.Equal(
            [FileItem.Directory("Отчёты 2025"), FileItem.File("Отчёты 2025/итог.pdf", 12345), FileItem.Directory("Отчёты 2025/пустая папка"),
             FileItem.File("Отчёты 2025/пустой.txt", 0), FileItem.File("фото 😀.jpg", 5_000_000_000)],
            offer.Items);
        Assert.Equal(3, offer.FileCount);
        Assert.Equal(2, offer.TopLevel.Count());
        AssertSameObject(sample, offer.ToMessage());
    }

    [Fact]
    public void RequestMessages()
    {
        AssertSameObject(SampleMessage("file_get"), FileMessages.Get(OfferId, 7, 4, 3_000_000_000));
        AssertSameObject(SampleMessage("file_end"), FileMessages.End(7, 2_000_000_000));
        AssertSameObject(SampleMessage("file_error"), FileMessages.Error(8, "changed"));
        AssertSameObject(SampleMessage("file_cancel"), FileMessages.Cancel(4_294_967_295));

        var get = SampleMessage("file_get");
        Assert.Equal(7u, FileMessages.Req(get));
        Assert.Equal((OfferId, 4, 3_000_000_000L), FileMessages.ParseGet(get));
        Assert.Equal(2_000_000_000, FileMessages.ParseEndSize(SampleMessage("file_end")));
        Assert.Equal("changed", FileMessages.ParseErrorReason(SampleMessage("file_error")));
        Assert.Equal(uint.MaxValue, FileMessages.Req(SampleMessage("file_cancel")));
    }

    [Fact]
    public void BadRequestFieldsDoNotBreakSession()
    {
        // Неверные index и offset — ответ not_found (−1), без req — пропуск.
        Assert.Equal(("x", -1, -1L), FileMessages.ParseGet(Parse("""{"t":"file_get","id":"x","req":1,"index":-1,"offset":"0"}""")));
        Assert.Throws<FileMessageException>(() => FileMessages.Req(Parse("""{"t":"file_get","id":"x","index":0,"offset":0}""")));
        Assert.Throws<FileMessageException>(() => FileMessages.Req(Parse("""{"t":"file_end","req":0,"size":0}""")));
        Assert.Throws<FileMessageException>(() => FileMessages.Req(Parse("""{"t":"file_cancel","req":4294967296}""")));
        Assert.Throws<FileMessageException>(() => FileMessages.ParseEndSize(Parse("""{"t":"file_end","req":1}""")));
        Assert.Throws<FileMessageException>(() => FileOffer.Parse(Parse($$"""{"t":"file_offer","id":"{{OfferId}}","total":0}""")));
        Assert.Equal("unavailable", FileMessages.ParseErrorReason(Parse("""{"t":"file_error","req":3}""")));
        Assert.Equal(FileTransferFailure.NotFound, FileTransferFailures.FromPeerReason("not_found"));
        Assert.Equal(FileTransferFailure.Changed, FileTransferFailures.FromPeerReason("changed"));
        Assert.Equal(FileTransferFailure.Unavailable, FileTransferFailures.FromPeerReason("новая"));
        // FileMessageException — ProtocolException, но узел ловит её отдельно и сеанс не рвёт.
        Assert.IsAssignableFrom<ProtocolException>(new FileMessageException("x"));
    }

    public static TheoryData<string> ValidOffers => [.. Section("file_offers")["valid"]!.AsArray().Select(entry => String(entry!["name"]))];

    public static TheoryData<string> InvalidOffers => [.. Section("file_offers")["invalid"]!.AsArray().Select(entry => String(entry!["name"]))];

    private static JsonObject OfferByName(string list, string name) =>
        Parse(String(Section("file_offers")[list]!.AsArray().Single(entry => String(entry!["name"]) == name)!["json"]));

    [Theory]
    [MemberData(nameof(ValidOffers))]
    public void ValidOfferIsAccepted(string name) => FileOffer.Parse(OfferByName("valid", name));

    [Theory]
    [MemberData(nameof(InvalidOffers))]
    public void InvalidOfferIsRejected(string name) =>
        Assert.Throws<FileMessageException>(() => FileOffer.Parse(OfferByName("invalid", name)));

    [Fact]
    public void ItemLimit()
    {
        var many = Enumerable.Range(0, 10_000).Select(index => FileItem.File($"f{index}", 1)).ToList();
        Assert.Null(FileOffer.Refusal(OfferId, many, 10_000));
        Assert.NotNull(FileOffer.Refusal(OfferId, [.. many, FileItem.File("f10000", 1)], 10_001));
        Assert.Equal(1_048_576, Protocol.FileChunkBytes);
        Assert.Equal(10_000, Protocol.MaxFileItems);
        Assert.Equal(10_737_418_240, Protocol.MaxFileTotalBytes);
        Assert.Equal("file", Protocol.FileCapability);
    }

    public static TheoryData<int> ChunkFrames => [.. Enumerable.Range(0, Root["file_chunk_frames"]!.AsArray().Count)];

    [Theory]
    [MemberData(nameof(ChunkFrames))]
    public void ChunkPlaintextAndCipher(int index)
    {
        var frame = Root["file_chunk_frames"]![index]!.AsObject();
        var req = frame["req"]!.GetValue<uint>();
        var data = Hex(frame["data"]);
        var plaintext = FileChunk.Plaintext(req, data);
        Assert.Equal(String(frame["plaintext"]), ToHex(plaintext));
        Assert.Equal((req, data.Length), FileChunk.Parse(plaintext));

        using var aes = new AesGcm(Hex(frame["key"]), 16);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(SecureChannel.Nonce(ulong.Parse(String(frame["counter"]))), plaintext, ciphertext, tag);
        Assert.Equal(String(frame["payload"]), ToHex([.. ciphertext, .. tag]));
    }

    [Fact]
    public void BadChunks()
    {
        Assert.Throws<FileMessageException>(() => FileChunk.Parse(FileChunk.Plaintext(1, [])));
        Assert.Throws<FileMessageException>(() => FileChunk.Parse(new byte[] { 0, 0, 1 }));
        Assert.Throws<FileMessageException>(() => FileChunk.Parse(FileChunk.Plaintext(1, new byte[1_048_577])));
        Assert.Equal((1u, 1_048_576), FileChunk.Parse(FileChunk.Plaintext(1, new byte[1_048_576])));
        // До шифрования 0x00 — не сообщение.
        Assert.Throws<ProtocolException>(() => Messages.Decode(FileChunk.Plaintext(1, [1])));
    }

    [Fact]
    public void LocalNamesMatchMac()
    {
        var names = Section("file_names");
        var items = names["items"]!.AsArray()
            .Select(entry => entry!["dir"] is not null
                ? FileItem.Directory(String(entry["path"]))
                : FileItem.File(String(entry["path"]), entry["size"]!.GetValue<long>()))
            .ToList();
        Assert.Equal(names["mac"]!.AsArray().Select(String), FileNames.LocalPaths(items, windows: false));
        Assert.Equal(names["windows"]!.AsArray().Select(String), FileNames.LocalPaths(items, windows: true));
        foreach (var entry in names["wire"]!.AsArray())
            Assert.Equal(String(entry!["wire"]), FileNames.WireName(Encoding.UTF8.GetString(Hex(entry["local_hex"]))));
        foreach (var entry in names["numbered"]!.AsArray())
        {
            Assert.Equal(String(entry!["result"]),
                FileNames.Numbered(String(entry["name"]), entry["number"]!.GetValue<int>(), entry["dir"]!.GetValue<bool>()));
        }
    }

    [Fact]
    public void FailureCodes()
    {
        Assert.Equal(FileTransferFailure.Cancelled, FileTransferFailures.Of(new OperationCanceledException()));
        Assert.Equal(FileTransferFailure.Changed, FileTransferFailures.Of(new FileTransferException(FileTransferFailure.Changed)));
        Assert.Equal(FileTransferFailure.DeviceUnavailable, FileTransferFailures.Of(new IOException("x")));
        Assert.IsAssignableFrom<IOException>(new FileTransferException(FileTransferFailure.NotFound));
    }
}

/// Обход папок у отправителя и кэш полученного — на временной папке (как verifyFileTree в clipvey-checks).
public sealed class FileTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clipvey-tests-" + Guid.NewGuid().ToString("N"));

    public FileTreeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, int bytes)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
    }

    [Fact]
    public void BuildSkipsLinksAndSpecialFiles()
    {
        Write("src/Папка/б.txt", 5);
        Write("src/Папка/а.txt", 3);
        Write("src/Папка/.DS_Store", 10);
        Directory.CreateDirectory(Path.Combine(_root, "src/Папка/пустая"));
        Write("src/Папка/вложенная/пустой", 0);
        File.CreateSymbolicLink(Path.Combine(_root, "src/Папка/ссылка"), Path.Combine(_root, "src/Папка/а.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "src/Папка/ссылка-на-папку"), Path.Combine(_root, "src/Папка/пустая"));
        Write("other/Папка", 7);
        var nfd = "йод.txt".Normalize(NormalizationForm.FormD);
        Write("src/" + nfd, 2);
        if (!OperatingSystem.IsWindows())
        {
            // «:» в пути Mac — это «/» в Finder; канал — особый файл.
            Write("src/Папка/12:05.txt", 1);
            using var mkfifo = Process.Start("mkfifo", Path.Combine(_root, "src/Папка/канал"));
            mkfifo.WaitForExit();
        }

        var tree = FileTree.Build([Path.Combine(_root, "src/Папка"), Path.Combine(_root, "other/Папка"), Path.Combine(_root, "src", nfd),
            Path.Combine(_root, "src/Папка/ссылка")]);
        var paths = tree.Items.Select(item => item.IsDirectory ? item.Path + "/" : $"{item.Path} {item.Size}").ToList();
        List<string> expected = OperatingSystem.IsWindows()
            ? ["Папка/", "Папка/а.txt 3", "Папка/б.txt 5", "Папка/вложенная/", "Папка/вложенная/пустой 0", "Папка/пустая/", "Папка (2) 7", "йод.txt 2"]
            : ["Папка/", "Папка/12_05.txt 1", "Папка/а.txt 3", "Папка/б.txt 5", "Папка/вложенная/", "Папка/вложенная/пустой 0",
               "Папка/пустая/", "Папка (2) 7", "йод.txt 2"];
        Assert.Equal(expected, paths);
        Assert.Equal(OperatingSystem.IsWindows() ? 17 : 18, tree.Total);
        Assert.Equal(tree.Items.Count, tree.LocalPaths.Count);
        Assert.Null(FileOffer.Refusal("0123456789abcdef0123456789abcdef", tree.Items, tree.Total));
    }

    [Fact]
    public void BuildFailures()
    {
        Write("src/a.txt", 1);
        File.CreateSymbolicLink(Path.Combine(_root, "src/ссылка"), Path.Combine(_root, "src/a.txt"));
        Assert.Equal(FileOfferFailure.Empty,
            Assert.Throws<FileOfferException>(() => FileTree.Build([Path.Combine(_root, "src/ссылка")])).Failure);
        Assert.Equal(FileOfferFailure.Empty, Assert.Throws<FileOfferException>(() => FileTree.Build([])).Failure);
        Assert.Equal(FileOfferFailure.Unreadable,
            Assert.Throws<FileOfferException>(() => FileTree.Build([Path.Combine(_root, "нет такого")])).Failure);

        var many = Path.Combine(_root, "many");
        Directory.CreateDirectory(many);
        for (var index = 0; index < 10_000; index++)
            File.WriteAllBytes(Path.Combine(many, $"f{index}"), []);
        Assert.Equal(FileOfferFailure.TooManyItems, Assert.Throws<FileOfferException>(() => FileTree.Build([many])).Failure);
        File.Delete(Path.Combine(many, "f0"));
        Assert.Equal(10_000, FileTree.Build([many]).Items.Count);
    }

    [Fact]
    public void IncomingCacheRemovesOnlyOld()
    {
        var cache = Path.Combine(_root, "Incoming");
        var old = IncomingCache.DirectoryFor(cache, "00000000000000000000000000000001");
        var fresh = IncomingCache.DirectoryFor(cache, "00000000000000000000000000000002");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        Directory.SetCreationTimeUtc(old, twoDaysAgo);
        Directory.SetLastWriteTimeUtc(old, twoDaysAgo);
        Assert.Equal(1, IncomingCache.Clean(cache));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.Equal(0, IncomingCache.Clean(Path.Combine(_root, "нет такой")));
    }
}

/// Двоичные куски через настоящий SecureChannel поверх TCP на 127.0.0.1: шифрование на месте, пул буферов,
/// JSON и куски вперемешку.
public class FileChunkFrameTests
{
    private static JsonObject Frame(int index) => Root["file_chunk_frames"]![index]!.AsObject();

    private const int SocketBuffer = 64 * 1024;

    private static async Task<(SecureChannel Channel, System.Net.Sockets.TcpClient Server)> OpenAsync(byte[] key, ulong counter)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        // Буферы сокетов маленькие и постоянные (без автонастройки системы): кадр в 1 МиБ в них не помещается,
        // поэтому проверка, которая пишет, не читая с другой стороны, зависает всегда, а не изредка.
        var client = new System.Net.Sockets.TcpClient { SendBufferSize = SocketBuffer, ReceiveBufferSize = SocketBuffer };
        await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        server.SendBufferSize = SocketBuffer;
        server.ReceiveBufferSize = SocketBuffer;
        listener.Stop();
        var channel = new SecureChannel(client, client.GetStream(), key, key);
        channel.SetCounters(counter, counter);
        return (channel, server);
    }

    private static async Task<byte[]> ReadFrameAsync(System.Net.Sockets.TcpClient server)
    {
        var header = new byte[4];
        await server.GetStream().ReadExactlyAsync(header);
        var payload = new byte[System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header)];
        await server.GetStream().ReadExactlyAsync(payload);
        return payload;
    }

    private static async Task WriteFrameAsync(System.Net.Sockets.TcpClient server, byte[] payload)
    {
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await server.GetStream().WriteAsync(header);
        await server.GetStream().WriteAsync(payload);
    }

    [Theory]
    [MemberData(nameof(FileProtocolTests.ChunkFrames), MemberType = typeof(FileProtocolTests))]
    public async Task SealAndOpen(int index)
    {
        var frame = Frame(index);
        var key = Hex(frame["key"]);
        var counter = ulong.Parse(String(frame["counter"]));
        var req = frame["req"]!.GetValue<uint>();
        var data = Hex(frame["data"]);

        var (sender, senderServer) = await OpenAsync(key, counter);
        await using (sender)
        using (senderServer)
        {
            await sender.SendChunkAsync(req, data, CancellationToken.None);
            Assert.Equal(String(frame["payload"]), ToHex(await ReadFrameAsync(senderServer)));
            // Без копии: данные прямо в буфере кадра.
            using var prepared = FileChunkFrame.Rent();
            data.CopyTo(prepared.Data);
            var (again, againServer) = await OpenAsync(key, counter);
            await using (again)
            using (againServer)
            {
                await again.SendChunkAsync(prepared, req, data.Length, CancellationToken.None);
                Assert.Equal(String(frame["payload"]), ToHex(await ReadFrameAsync(againServer)));
            }
        }

        var (receiver, receiverServer) = await OpenAsync(key, counter);
        await using (receiver)
        using (receiverServer)
        {
            await WriteFrameAsync(receiverServer, Hex(frame["payload"]));
            using var received = await receiver.ReceiveFrameAsync(CancellationToken.None);
            Assert.True(received.IsChunk);
            Assert.Equal(req, received.Req);
            Assert.Equal(ToHex(data), ToHex(received.ChunkData.Span));
        }
    }

    [Fact]
    public async Task ChunksAndMessagesInterleave()
    {
        var key = Hex(Section("session")["k_ir"]);
        var (a, aServer) = await OpenAsync(key, 0);
        var (b, bServer) = await OpenAsync(key, 0);
        await using (a)
        await using (b)
        using (aServer)
        using (bServer)
        {
            // Что отправил a, пересылаем в b как есть: b расшифрует теми же ключом и счётчиками.
            // Отправка, пересылка и приём идут одновременно: кадр в 1 МиБ больше буферов сокетов, и запись
            // без читателя на другой стороне ждала бы вечно (так проверка и зависала — изредка, когда система
            // не успевала увеличить буферы сама).
            var big = RandomNumberGenerator.GetBytes(Protocol.FileChunkBytes);
            var send = Task.Run(async () =>
            {
                await a.SendChunkAsync(7, big, CancellationToken.None);
                await a.SendAsync(new JsonObject { ["t"] = "ping" }, CancellationToken.None);
                await a.SendChunkAsync(8, new byte[] { 1, 2, 3 }, CancellationToken.None);
            });
            var forward = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                    await WriteFrameAsync(bServer, await ReadFrameAsync(aServer));
            });

            var timeout = TimeSpan.FromSeconds(10);
            using (var first = await b.ReceiveFrameAsync(CancellationToken.None).WaitAsync(timeout))
            {
                Assert.Equal(7u, first.Req);
                Assert.True(first.ChunkData.Span.SequenceEqual(big));
                // Кусок можно забрать: кадр его больше не освобождает.
                using var taken = first.TakeChunk();
                Assert.Equal(big.Length, taken.Length);
            }
            using (var ping = await b.ReceiveFrameAsync(CancellationToken.None).WaitAsync(timeout))
                Assert.Equal("ping", Messages.Type(ping.Message!));
            using (var last = await b.ReceiveFrameAsync(CancellationToken.None).WaitAsync(timeout))
                Assert.Equal(new byte[] { 1, 2, 3 }, last.ChunkData.ToArray());
            await send.WaitAsync(timeout);
            await forward.WaitAsync(timeout);
        }
    }

    [Fact]
    public async Task BadChunkIsSkippedAndHandshakeRejectsBinary()
    {
        var key = Hex(Section("session")["k_ir"]);
        using var aes = new AesGcm(key, 16);
        byte[] Seal(byte[] plaintext, ulong counter)
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            aes.Encrypt(SecureChannel.Nonce(counter), plaintext, ciphertext, tag);
            return [.. ciphertext, .. tag];
        }
        var (channel, server) = await OpenAsync(key, 0);
        await using (channel)
        using (server)
        {
            await WriteFrameAsync(server, Seal([0, 0, 0, 0, 1], 0));
            using (var invalid = await channel.ReceiveFrameAsync(CancellationToken.None))
            {
                Assert.NotNull(invalid.Invalid);
                Assert.False(invalid.IsChunk);
            }
            await WriteFrameAsync(server, Seal(FileChunk.Plaintext(3, [9]), 1));
            await Assert.ThrowsAsync<ProtocolException>(() => channel.ReceiveAsync(CancellationToken.None));
        }
    }
}
