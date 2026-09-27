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
