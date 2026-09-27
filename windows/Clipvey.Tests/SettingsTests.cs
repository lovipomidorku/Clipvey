using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Clipvey.Core;

namespace Clipvey.Tests;

/// Общие настройки (docs/protocol.md, «Общие настройки»): значения, «кто новее», разбор без полей.
public class SharedSettingsTests
{
    [Theory]
    [InlineData(-5, 50)]
    [InlineData(0, 50)]
    [InlineData(50, 50)]
    [InlineData(75, 50)]      // посередине — меньшее
    [InlineData(76, 100)]
    [InlineData(200, 100)]
    [InlineData(201, 300)]
    [InlineData(400, 300)]
    [InlineData(750, 500)]
    [InlineData(751, 1000)]
    [InlineData(5620, 1000)]
    [InlineData(5621, 10240)]
    [InlineData(10240, 10240)]
    [InlineData(int.MaxValue, 10240)]
    public void Nearest(int mb, int expected) => Assert.Equal(expected, SharedSettings.Nearest(mb));

    [Fact]
    public void AllowedValuesAndAlways()
    {
        Assert.Equal([50, 100, 300, 500, 1000, 10240], SharedSettings.AllowedMB);
        foreach (var allowed in SharedSettings.AllowedMB)
            Assert.Equal(allowed, SharedSettings.Nearest(allowed));
        // «Всегда» — весь допустимый размер описания.
        Assert.Equal(Protocol.MaxFileTotalBytes, new SharedSettings(SharedSettings.AlwaysMB, 0, "x").AutoDownloadBytes);
        Assert.Equal(50L * 1024 * 1024, SharedSettings.Default("x").AutoDownloadBytes);
        Assert.Equal(new SharedSettings(50, 0, "abc"), SharedSettings.Default("abc"));
    }

    [Fact]
    public void NewerByChangedThenBy()
    {
        var a = new SharedSettings(100, 5000, "aaaa");
        Assert.True(new SharedSettings(50, 5001, "0000").IsNewerThan(a));
        Assert.False(new SharedSettings(300, 4999, "ffff").IsNewerThan(a));
        Assert.True(new SharedSettings(50, 5000, "aaab").IsNewerThan(a));
        Assert.False(new SharedSettings(300, 5000, "aaa").IsNewerThan(a));
        // Такие же — не новее (пропускаются, дальше не пересылаются).
        Assert.False(a.IsNewerThan(a with { }));
        // Сравнение строк порядковое: заглавные раньше строчных.
        Assert.True(new SharedSettings(50, 1, "a").IsNewerThan(new SharedSettings(50, 1, "B")));
    }

    private static SharedSettings Parse(string json) => SharedSettings.Parse(Messages.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ParseAndEncode()
    {
        var settings = Parse("""{"t":"settings","autoDownloadMB":300,"changed":1790000000000,"by":"ec2b4635da6e02fc8b8f6095a12ceb88"}""");
        Assert.Equal(new SharedSettings(300, 1790000000000, "ec2b4635da6e02fc8b8f6095a12ceb88"), settings);
        var encoded = JsonNode.Parse(Messages.Encode(settings.ToMessage()))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(
            """{"t":"settings","autoDownloadMB":300,"changed":1790000000000,"by":"ec2b4635da6e02fc8b8f6095a12ceb88"}"""), encoded));
    }

    [Theory]
    [InlineData("""{"t":"settings"}""", 50, 0, "")]
    [InlineData("""{"t":"settings","autoDownloadMB":100}""", 100, 0, "")]
    [InlineData("""{"t":"settings","changed":7,"by":"x"}""", 50, 7, "x")]
    [InlineData("""{"t":"settings","autoDownloadMB":2000,"changed":7,"by":"x"}""", 1000, 7, "x")]
    [InlineData("""{"t":"settings","autoDownloadMB":99.5,"changed":7,"by":"x"}""", 100, 7, "x")]
    [InlineData("""{"t":"settings","autoDownloadMB":1e20,"changed":7,"by":"x"}""", 10240, 7, "x")]
    [InlineData("""{"t":"settings","autoDownloadMB":"300","changed":"7","by":5}""", 50, 0, "")]
    [InlineData("""{"t":"settings","autoDownloadMB":true,"changed":-3,"by":null}""", 50, 0, "")]
    [InlineData("""{"t":"settings","autoDownloadMB":300,"changed":1.5,"by":"x"}""", 300, 0, "x")]
    public void ParseWithoutOrWithWrongFields(string json, int mb, long changed, string by) =>
        Assert.Equal(new SharedSettings(mb, changed, by), Parse(json));

    [Fact]
    public void MessageWithoutFieldsIsNeverNewer() =>
        Assert.False(Parse("""{"t":"settings","autoDownloadMB":10240}""").IsNewerThan(SharedSettings.Default("0")));

    [Fact]
    public void NormalizedFromHost()
    {
        Assert.Equal(new SharedSettings(50, 0, "me"), new SharedSettings(75, -1, null!).Normalized("me"));
        Assert.Equal(new SharedSettings(500, 9, "x"), new SharedSettings(500, 9, "x").Normalized("me"));
    }
}

/// Несколько настоящих узлов в одном процессе на 127.0.0.1; связи (links) записаны в списки устройств сразу.
internal sealed class NodeMesh : IAsyncDisposable
{
    private readonly List<IDisposable> _identities = [];
    private string _root = "";

    public List<ClipveyNode> Nodes { get; } = [];

    /// События SettingsChanged каждого узла по порядку.
    public List<ConcurrentQueue<SharedSettings>> Events { get; } = [];

    public static async Task<NodeMesh> StartAsync(int count, (int A, int B)[] links, Func<int, string, SharedSettings?>? settings = null)
    {
        var mesh = new NodeMesh { _root = Path.Combine(Path.GetTempPath(), "clipvey-settings-" + Guid.NewGuid().ToString("N")) };
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var identities = Enumerable.Range(0, count)
            .Select(index => Identity.LoadOrCreate(new FileSecretStore(Path.Combine(mesh._root, $"n{index}"))))
            .ToList();
        mesh._identities.AddRange(identities);
        var ports = Enumerable.Range(0, count).Select(_ => FreePort()).ToList();
        var stores = Enumerable.Range(0, count).Select(index => new DeviceStore(Path.Combine(mesh._root, $"n{index}"))).ToList();
        foreach (var (a, b) in links)
        {
            stores[a].Upsert(new StoredDevice(identities[b].DeviceId, $"test-n{b}-{suffix}", Convert.ToBase64String(identities[b].PublicKey), "127.0.0.1", ports[b]));
            stores[b].Upsert(new StoredDevice(identities[a].DeviceId, $"test-n{a}-{suffix}", Convert.ToBase64String(identities[a].PublicKey), "127.0.0.1", ports[a]));
        }
        for (var index = 0; index < count; index++)
        {
            var node = new ClipveyNode(identities[index], stores[index], $"test-n{index}-{suffix}", ports[index],
                settings: settings?.Invoke(index, identities[index].DeviceId));
            var events = new ConcurrentQueue<SharedSettings>();
            node.SettingsChanged += events.Enqueue;
            mesh.Nodes.Add(node);
            mesh.Events.Add(events);
        }
        foreach (var node in mesh.Nodes)
            node.Start();
        var expected = Enumerable.Range(0, count).Select(index => links.Count(link => link.A == index || link.B == index)).ToList();
        await Until(() => Enumerable.Range(0, count).All(index => mesh.Nodes[index].Devices.Count(device => device.Connected) == expected[index]),
            TimeSpan.FromSeconds(30), "узлы не подключились");
        return mesh;
    }

    public static async Task Until(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail(failure);
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in Nodes)
            await node.DisposeAsync();
        foreach (var identity in _identities)
            identity.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка — не страшно.
        }
    }
}

/// Общие настройки между настоящими узлами: рассылка, пересылка по цепочке, «кто новее» при подключении.
public class SharedSettingsNodeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static Task AllEqual(NodeMesh mesh, SharedSettings expected) =>
        NodeMesh.Until(() => mesh.Nodes.All(node => node.Settings == expected), Timeout,
            $"настройки не сошлись к {expected}: {string.Join(" | ", mesh.Nodes.Select(node => node.Settings))}");

    [Fact]
    public async Task DefaultsConvergeToLargerDeviceId()
    {
        await using var mesh = await NodeMesh.StartAsync(2, [(0, 1)]);
        var larger = mesh.Nodes.Select(node => node.DeviceId).Max(StringComparer.Ordinal)!;
        await AllEqual(mesh, new SharedSettings(50, 0, larger));
        // Значение не поменялось, но by — другое: меньший узел сохраняет всю тройку.
        var smaller = mesh.Nodes.Single(node => node.DeviceId != larger);
        await NodeMesh.Until(() => mesh.Events[mesh.Nodes.IndexOf(smaller)].Count == 1, Timeout, "меньший узел не сообщил о принятых настройках");
        Assert.Empty(mesh.Events[mesh.Nodes.IndexOf(mesh.Nodes.Single(node => node.DeviceId == larger))]);
    }

    [Fact]
    public async Task ChangeReachesWholeChain()
    {
        // 0 — 1 — 2: 0 и 2 не связаны, изменение доходит через 1.
        await using var mesh = await NodeMesh.StartAsync(3, [(0, 1), (1, 2)]);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        mesh.Nodes[0].SetAutoDownloadMB(300);
        var changed = mesh.Nodes[0].Settings;
        Assert.Equal(300, changed.AutoDownloadMB);
        Assert.Equal(mesh.Nodes[0].DeviceId, changed.By);
        Assert.InRange(changed.Changed, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(changed, mesh.Events[0].Last());
        await AllEqual(mesh, changed);
        await NodeMesh.Until(() => mesh.Events[2].LastOrDefault() == changed, Timeout, "у конца цепочки не было события");

        // Незнакомое значение — ближайшее допустимое; «Всегда».
        mesh.Nodes[2].SetAutoDownloadMB(20000);
        await AllEqual(mesh, mesh.Nodes[2].Settings);
        Assert.Equal(10240, mesh.Nodes[0].Settings.AutoDownloadMB);
        Assert.Equal(Protocol.MaxFileTotalBytes, mesh.Nodes[0].Settings.AutoDownloadBytes);
    }

    [Fact]
    public async Task SameValueChangesNothing()
    {
        await using var mesh = await NodeMesh.StartAsync(1, []);
        var node = mesh.Nodes[0];
        node.SetAutoDownloadMB(50);
        node.SetAutoDownloadMB(60);
        Assert.Equal(SharedSettings.Default(node.DeviceId), node.Settings);
        Assert.Empty(mesh.Events[0]);
    }

    [Fact]
    public async Task NewerWinsWhenDevicesConnect()
    {
        // Как выключенное устройство: у каждого своё сохранённое, при подключении побеждает более новое — у обоих.
        await using var mesh = await NodeMesh.StartAsync(3, [(0, 1), (1, 2)], (index, _) => index switch
        {
            0 => new SharedSettings(100, 1000, "x"),
            1 => new SharedSettings(1000, 3000, "z"),
            _ => new SharedSettings(500, 2000, "y"),
        });
        await AllEqual(mesh, new SharedSettings(1000, 3000, "z"));
        await NodeMesh.Until(() => !mesh.Events[0].IsEmpty && !mesh.Events[2].IsEmpty, Timeout, "не было событий о принятых настройках");
        Assert.Empty(mesh.Events[1]);
        Assert.Equal(new SharedSettings(1000, 3000, "z"), Assert.Single(mesh.Events[0]));
        Assert.Equal(new SharedSettings(1000, 3000, "z"), Assert.Single(mesh.Events[2]));
    }

    [Fact]
    public async Task EqualChangedLargerByWins()
    {
        var smallBy = new string('a', 32);
        var largeBy = new string('f', 32);
        await using var mesh = await NodeMesh.StartAsync(2, [(0, 1)], (index, _) =>
            index == 0 ? new SharedSettings(300, 5000, largeBy) : new SharedSettings(100, 5000, smallBy));
        await AllEqual(mesh, new SharedSettings(300, 5000, largeBy));
    }

    [Fact]
    public async Task LocalChangeBeatsFutureTimestamp()
    {
        // Часы другого устройства спешат: принятое изменение «из будущего» не перекрывает то, что сделали здесь потом.
        var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        await using var mesh = await NodeMesh.StartAsync(2, [(0, 1)], (index, _) =>
            index == 0 ? new SharedSettings(500, future, "zzzz") : null);
        await AllEqual(mesh, new SharedSettings(500, future, "zzzz"));
        mesh.Nodes[1].SetAutoDownloadMB(100);
        Assert.Equal(new SharedSettings(100, future + 1, mesh.Nodes[1].DeviceId), mesh.Nodes[1].Settings);
        await AllEqual(mesh, mesh.Nodes[1].Settings);
    }

    [Fact]
    public async Task SimultaneousChangesConverge()
    {
        await using var mesh = await NodeMesh.StartAsync(3, [(0, 1), (1, 2)]);
        await Task.WhenAll(
            Task.Run(() => mesh.Nodes[0].SetAutoDownloadMB(100)),
            Task.Run(() => mesh.Nodes[2].SetAutoDownloadMB(1000)));
        await NodeMesh.Until(() => mesh.Nodes.Select(node => node.Settings).Distinct().Count() == 1
            && mesh.Nodes[0].Settings.Changed > 0, Timeout,
            $"не сошлись: {string.Join(" | ", mesh.Nodes.Select(node => node.Settings))}");
        var result = mesh.Nodes[0].Settings;
        Assert.Contains(result.AutoDownloadMB, new[] { 100, 1000 });
        // Последнее событие у каждого — итог (события идут по порядку изменений).
        await NodeMesh.Until(() => mesh.Events.All(events => events.LastOrDefault() == result), Timeout, "последнее событие — не итог");
    }
}
