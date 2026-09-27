using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Clipvey.Core;

namespace Clipvey.Tests;

/// Два узла в одном процессе на 127.0.0.1: связь записана в списки устройств сразу, без связывания.
public sealed class NodePair : IAsyncDisposable
{
    public required string Root { get; init; }
    public required ClipveyNode Source { get; init; }
    public required ClipveyNode Receiver { get; init; }
    private readonly List<IDisposable> _identities = [];

    public string Files => Path.Combine(Root, "files");

    public static async Task<NodePair> StartAsync()
    {
        if (Environment.GetEnvironmentVariable("CLIPVEY_TEST_LOG") is { } logFile)
        {
            var logLock = new object();
            Log.Sink = line =>
            {
                lock (logLock)
                    File.AppendAllText(logFile, line + "\n");
            };
        }
        var root = Path.Combine(Path.GetTempPath(), "clipvey-transfer-" + Guid.NewGuid().ToString("N"));
        var source = Identity.LoadOrCreate(new FileSecretStore(Path.Combine(root, "source")));
        var receiver = Identity.LoadOrCreate(new FileSecretStore(Path.Combine(root, "receiver")));
        var sourcePort = FreePort();
        var receiverPort = FreePort();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sourceStore = new DeviceStore(Path.Combine(root, "source"));
        var receiverStore = new DeviceStore(Path.Combine(root, "receiver"));
        sourceStore.Upsert(new StoredDevice(receiver.DeviceId, $"test-receiver-{suffix}", Convert.ToBase64String(receiver.PublicKey), "127.0.0.1", receiverPort));
        receiverStore.Upsert(new StoredDevice(source.DeviceId, $"test-source-{suffix}", Convert.ToBase64String(source.PublicKey), "127.0.0.1", sourcePort));
        var pair = new NodePair
        {
            Root = root,
            Source = new ClipveyNode(source, sourceStore, $"test-source-{suffix}", sourcePort),
            Receiver = new ClipveyNode(receiver, receiverStore, $"test-receiver-{suffix}", receiverPort),
        };
        pair._identities.Add(source);
        pair._identities.Add(receiver);
        Directory.CreateDirectory(pair.Files);
        pair.Source.Start();
        pair.Receiver.Start();
        for (var i = 0; i < 200 && !(Connected(pair.Source) && Connected(pair.Receiver)); i++)
            await Task.Delay(100);
        Assert.True(Connected(pair.Source) && Connected(pair.Receiver), "узлы не подключились за 20 с");
        // Caps другой стороны приходит в ready: к этому моменту оба знают про file.
        Assert.True(pair.Source.Devices.Single().AcceptsFiles);
        return pair;

        static bool Connected(ClipveyNode node) => node.Devices.Any(device => device.Connected);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public string SourceId => Source.DeviceId;

    /// Отправить описание и дождаться его у получателя.
    public async Task<FileOffer> OfferAsync(params string[] paths)
    {
        var received = new TaskCompletionSource<FileOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOffer(FileOfferReceived offer) => received.TrySetResult(offer.Offer);
        Receiver.FileOffered += OnOffer;
        try
        {
            var result = await Source.OfferFilesAsync(paths);
            Assert.Null(result.Failure);
            Assert.Equal(1, result.Recipients);
            var offer = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(result.Offer!.Id, offer.Id);
            return offer;
        }
        finally
        {
            Receiver.FileOffered -= OnOffer;
        }
    }

    public byte[] WriteRandom(string name, int length)
    {
        var data = RandomNumberGenerator.GetBytes(length);
        var path = Path.Combine(Files, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return data;
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await Receiver.DisposeAsync();
        foreach (var identity in _identities)
            identity.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка — не страшно.
        }
    }
}

/// Узлы для нескольких проверок сразу: подключение занимает пару секунд.
public sealed class NodePairFixture : IAsyncLifetime
{
    public NodePair Pair { get; private set; } = null!;

    public async Task InitializeAsync() => Pair = await NodePair.StartAsync();

    public async Task DisposeAsync() => await Pair.DisposeAsync();
}

/// Скачивание и потоки OpenFileStream между двумя настоящими узлами.
public class FileTransferTests(NodePairFixture fixture) : IClassFixture<NodePairFixture>
{
    private NodePair Pair => fixture.Pair;

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    [Fact]
    public async Task StreamWithSlowReaderAndSeek()
    {
        var data = Pair.WriteRandom("медленный.bin", 40 * 1024 * 1024 + 123);
        var offer = await Pair.OfferAsync(Path.Combine(Pair.Files, "медленный.bin"));
        using var stream = Pair.Receiver.OpenFileStream(offer, Pair.SourceId, 0);
        Assert.Equal(data.Length, stream.Length);
        Assert.True(stream.CanSeek);
        Assert.Equal(data[..100], ReadExactly(stream, 100));
        // Читатель отстал: источник успевает прислать больше 16 МиБ — запрос отменяется и потом возобновляется.
        // Сеанс при этом не ждёт читателя: текст проходит.
        var clip = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClip(string text, string from) => clip.TrySetResult(text);
        Pair.Receiver.ClipReceived += OnClip;
        try
        {
            await Task.Delay(500);
            await Pair.Source.BroadcastClipAsync("пока поток ждёт");
            Assert.Equal("пока поток ждёт", await clip.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Pair.Receiver.ClipReceived -= OnClip;
        }
        await Task.Delay(1000);
        var rest = new MemoryStream();
        stream.CopyTo(rest, 64 * 1024);
        Assert.True(rest.ToArray().AsSpan().SequenceEqual(data.AsSpan(100)));
        Assert.Equal(0, stream.Read(new byte[10]));

        stream.Position = 30 * 1024 * 1024;
        Assert.Equal(data.AsSpan(30 * 1024 * 1024, 1024 * 1024).ToArray(), ReadExactly(stream, 1024 * 1024));
        stream.Seek(1000, SeekOrigin.Begin);
        Assert.Equal(data[1000..1010], ReadExactly(stream, 10));
        // Вперёд в пределах уже полученного.
        stream.Seek(5, SeekOrigin.Current);
        Assert.Equal(data[1015..1020], ReadExactly(stream, 5));
        stream.Seek(-3, SeekOrigin.End);
        Assert.Equal(data[^3..], ReadExactly(stream, 3));
    }

    [Fact]
    public async Task ConcurrentStreamsOnThreads()
    {
        var files = Enumerable.Range(0, 4).Select(i => Pair.WriteRandom($"параллельно/{i}.bin", 5 * 1024 * 1024 + i)).ToList();
        var offer = await Pair.OfferAsync(Path.Combine(Pair.Files, "параллельно"));
        var results = new byte[4][];
        var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            var index = offer.Items.ToList().FindIndex(item => item.Path == $"параллельно/{i}.bin");
            using var stream = Pair.Receiver.OpenFileStream(offer, Pair.SourceId, index);
            var copy = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                copy.Write(buffer, 0, read);
            results[i] = copy.ToArray();
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join(TimeSpan.FromSeconds(60)));
        for (var i = 0; i < 4; i++)
            Assert.True(files[i].AsSpan().SequenceEqual(results[i]), $"файл {i}");
    }

    [Fact]
    public async Task DownloadKeepsStructureAndNumbersTakenNames()
    {
        var data = Pair.WriteRandom("скачать/вложенная/данные.bin", 3_000_000);
        File.WriteAllBytes(Path.Combine(Pair.Files, "скачать", "пустой"), []);
        Directory.CreateDirectory(Path.Combine(Pair.Files, "скачать", "пустая папка"));
        var offer = await Pair.OfferAsync(Path.Combine(Pair.Files, "скачать"));
        var target = Path.Combine(Pair.Root, "target-" + offer.Id);
        Directory.CreateDirectory(Path.Combine(target, "скачать"));
        long lastProgress = 0;
        var result = await Pair.Receiver.DownloadFilesAsync(offer, Pair.SourceId, target,
            new SyncProgress(total => Interlocked.Exchange(ref lastProgress, total)), CancellationToken.None);
        var top = Assert.Single(result);
        Assert.Equal(Path.Combine(target, "скачать (2)"), top);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(top, "вложенная", "данные.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(top, "пустой")));
        Assert.True(Directory.Exists(Path.Combine(top, "пустая папка")));
        Assert.Equal(offer.Total, Interlocked.Read(ref lastProgress));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(target), entry => Path.GetFileName(entry).StartsWith(".clipvey-"));
    }

    [Fact]
    public async Task ChangedAndCancelledAndNotFound()
    {
        Pair.WriteRandom("ошибки/меняется.bin", 1000);
        var changedOffer = await Pair.OfferAsync(Path.Combine(Pair.Files, "ошибки", "меняется.bin"));
        File.AppendAllText(Path.Combine(Pair.Files, "ошибки", "меняется.bin"), "x");
        var target = Path.Combine(Pair.Root, "errors");
        var changed = await Assert.ThrowsAsync<FileTransferException>(() =>
            Pair.Receiver.DownloadFilesAsync(changedOffer, Pair.SourceId, target, null, CancellationToken.None));
        Assert.Equal(FileTransferFailure.Changed, changed.Failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

        // Отмена: скачивание прерывается, недокачанное удаляется, следующий запрос по тому же сеансу проходит.
        var data = Pair.WriteRandom("ошибки/большой.bin", 64 * 1024 * 1024);
        var big = await Pair.OfferAsync(Path.Combine(Pair.Files, "ошибки", "большой.bin"));
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new SyncProgress(total =>
            {
                if (total > 8 * 1024 * 1024)
                    cancel.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Pair.Receiver.DownloadFilesAsync(big, Pair.SourceId, target, progress, cancel.Token));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        using (var stream = Pair.Receiver.OpenFileStream(big, Pair.SourceId, 0))
        {
            stream.Position = data.Length - 10;
            Assert.Equal(data[^10..], ReadExactly(stream, 10));
        }

        // Выключенные файлы у источника: на запросы — not_found.
        Pair.Source.SetFilesEnabled(false);
        try
        {
            var notFound = await Assert.ThrowsAsync<FileTransferException>(() =>
                Pair.Receiver.DownloadFilesAsync(big, Pair.SourceId, target, null, CancellationToken.None));
            Assert.Equal(FileTransferFailure.NotFound, notFound.Failure);
            Assert.Equal(FileOfferFailure.Disabled, (await Pair.Source.OfferFilesAsync([Pair.Files])).Failure);
        }
        finally
        {
            Pair.Source.SetFilesEnabled(true);
        }
        Assert.Equal(FileOfferFailure.Empty, (await Pair.Source.OfferFilesAsync([])).Failure);
        Assert.Throws<FileTransferException>(() => Pair.Receiver.OpenFileStream(big, "00000000000000000000000000000000", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pair.Receiver.OpenFileStream(big, Pair.SourceId, 1));
    }
}

/// Обрыв сеанса: ожидающий поток и скачивание получают DeviceUnavailable.
public class FileTransferSessionLossTests
{
    [Fact]
    public async Task StreamFailsWhenSourceGoesAway()
    {
        await using var pair = await NodePair.StartAsync();
        pair.WriteRandom("уйдёт.bin", 8 * 1024 * 1024);
        var offer = await pair.OfferAsync(Path.Combine(pair.Files, "уйдёт.bin"));
        using var stream = pair.Receiver.OpenFileStream(offer, pair.SourceId, 0);
        stream.ReadExactly(new byte[10]);
        await pair.Source.DisposeAsync();
        var error = Assert.Throws<FileTransferException>(() =>
        {
            var buffer = new byte[1024 * 1024];
            while (stream.Read(buffer) > 0)
            {
            }
        });
        Assert.Equal(FileTransferFailure.DeviceUnavailable, error.Failure);
        var download = await Assert.ThrowsAsync<FileTransferException>(() =>
            pair.Receiver.DownloadFilesAsync(offer, pair.SourceId, Path.Combine(pair.Root, "t"), null, CancellationToken.None));
        Assert.Equal(FileTransferFailure.DeviceUnavailable, download.Failure);
    }
}

/// IProgress без переноса в контекст синхронизации.
internal sealed class SyncProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
