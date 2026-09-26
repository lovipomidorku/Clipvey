using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Clipvey.Core;
using static Clipvey.Tests.Vectors;

namespace Clipvey.Tests;

/// Ключи, связывание и сеанс по docs/protocol.md — те же значения, что проверяет Mac.
public class CryptoTests
{
    public static TheoryData<string> KeyNames => ["initiator_static", "responder_static", "initiator_ephemeral", "responder_ephemeral"];

    [Theory]
    [MemberData(nameof(KeyNames))]
    public void PublicKeyEncodingAndImport(string name)
    {
        using var key = PrivateKey(name);
        Assert.Equal(ToHex(PublicKey(name)), ToHex(P256.EncodePublicKey(key)));
        using var imported = P256.ImportPublicKey(PublicKey(name));
        Assert.Equal(ToHex(PublicKey(name)), ToHex(P256.EncodePublicKey(imported)));
    }

    [Fact]
    public void PointNotOnCurveIsRejected()
    {
        var bad = new byte[65];
        bad[0] = 0x04;
        bad.AsSpan(1).Fill(1);
        Assert.Throws<ProtocolException>(() => P256.ImportPublicKey(bad));
        Assert.Throws<ProtocolException>(() => P256.ImportPublicKey(PublicKey("initiator_static")[..64]));
    }

    [Fact]
    public void DeviceIds()
    {
        Assert.Equal(String(Section("device_id")["initiator"]), Identity.DeviceIdFor(PublicKey("initiator_static")));
        Assert.Equal(String(Section("device_id")["responder"]), Identity.DeviceIdFor(PublicKey("responder_static")));
    }

    [Fact]
    public void CommitmentAndCode()
    {
        var pairing = Section("pairing");
        var pkI = PublicKey("initiator_static");
        var pkR = PublicKey("responder_static");
        var nonceI = Hex(pairing["initiator_nonce"]);
        var nonceR = Hex(pairing["responder_nonce"]);

        Assert.Equal(String(pairing["commit"]), ToHex(PairingMath.Commitment(pkR, pkI, nonceR)));
        var code = PairingMath.Code(pkI, pkR, nonceI, nonceR);
        Assert.Equal(String(pairing["code"]), code);
        // Данные подобраны так, чтобы код начинался с нуля: проверяется дополнение до 6 цифр.
        Assert.Matches("^0[0-9]{5}$", code);
        Assert.NotEqual(code, PairingMath.Code(pkR, pkI, nonceR, nonceI));
    }

    [Fact]
    public void SessionKeysFromBothSides()
    {
        var session = Section("session");
        using var sI = PrivateKey("initiator_static");
        using var sR = PrivateKey("responder_static");
        using var eI = PrivateKey("initiator_ephemeral");
        using var eR = PrivateKey("responder_ephemeral");
        byte[] Agree(ECDiffieHellman own, string peer)
        {
            using var peerKey = P256.ImportPublicKey(PublicKey(peer));
            return P256.Agree(own, peerKey);
        }

        // Таблица из docs/protocol.md: I и R должны получить одно и то же.
        var dh1 = Agree(eI, "responder_ephemeral");
        var dh2 = Agree(sI, "responder_ephemeral");
        var dh3 = Agree(eI, "responder_static");
        Assert.Equal(String(session["dh1"]), ToHex(dh1));
        Assert.Equal(String(session["dh2"]), ToHex(dh2));
        Assert.Equal(String(session["dh3"]), ToHex(dh3));
        Assert.Equal(String(session["dh1"]), ToHex(Agree(eR, "initiator_ephemeral")));
        Assert.Equal(String(session["dh2"]), ToHex(Agree(eR, "initiator_static")));
        Assert.Equal(String(session["dh3"]), ToHex(Agree(sR, "initiator_ephemeral")));

        var th = PairingMath.SessionTranscript(
            PublicKey("initiator_static"), PublicKey("responder_static"),
            PublicKey("initiator_ephemeral"), PublicKey("responder_ephemeral"));
        Assert.Equal(String(session["th"]), ToHex(th));

        var (kIR, kRI) = PairingMath.SessionKeys(dh1, dh2, dh3, th);
        Assert.Equal(String(session["okm"]), ToHex([.. kIR, .. kRI]));
        Assert.Equal(String(session["k_ir"]), ToHex(kIR));
        Assert.Equal(String(session["k_ri"]), ToHex(kRI));
    }
}

/// Шифрованные кадры через настоящий SecureChannel поверх TCP на 127.0.0.1.
public class FrameTests
{
    public static TheoryData<int> FrameIndexes => [0, 1];

    private static JsonObject Frame(int index) => Root["frames"]![index]!.AsObject();

    private sealed class Loopback : IAsyncDisposable
    {
        public required SecureChannel Channel { get; init; }
        public required TcpClient Server { get; init; }
        public NetworkStream Remote => Server.GetStream();

        public static async Task<Loopback> OpenAsync(byte[] key, ulong counter)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            var channel = new SecureChannel(client, client.GetStream(), key, key);
            channel.SetCounters(counter, counter);
            return new Loopback { Channel = channel, Server = server };
        }

        public async Task<byte[]> ReadFrameAsync()
        {
            var header = new byte[4];
            await Remote.ReadExactlyAsync(header);
            var payload = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
            await Remote.ReadExactlyAsync(payload);
            return payload;
        }

        public async Task WriteFrameAsync(byte[] payload)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            await Remote.WriteAsync(header);
            await Remote.WriteAsync(payload);
        }

        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            Server.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(FrameIndexes))]
    public void Nonce(int index)
    {
        var frame = Frame(index);
        Assert.Equal(String(frame["nonce"]), ToHex(SecureChannel.Nonce(ulong.Parse(String(frame["counter"])))));
    }

    [Theory]
    [MemberData(nameof(FrameIndexes))]
    public async Task Seal(int index)
    {
        var frame = Frame(index);
        var plaintext = Hex(frame["plaintext"]);
        var message = JsonNode.Parse(plaintext)!.AsObject();
        // Образец открытого текста совпадает с тем, что закодирует .NET, — иначе шифротекст был бы другим.
        Assert.Equal(ToHex(plaintext), ToHex(Messages.Encode(message)));

        await using var loop = await Loopback.OpenAsync(Hex(frame["key"]), ulong.Parse(String(frame["counter"])));
        await loop.Channel.SendAsync(message, CancellationToken.None);
        Assert.Equal(String(frame["payload"]), ToHex(await loop.ReadFrameAsync()));
    }

    [Theory]
    [MemberData(nameof(FrameIndexes))]
    public async Task Open(int index)
    {
        var frame = Frame(index);
        await using var loop = await Loopback.OpenAsync(Hex(frame["key"]), ulong.Parse(String(frame["counter"])));
        await loop.WriteFrameAsync(Hex(frame["payload"]));
        var received = await loop.Channel.ReceiveAsync(CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Hex(frame["plaintext"])), received));
    }

    [Theory]
    [MemberData(nameof(FrameIndexes))]
    public async Task WrongCounterIsRejected(int index)
    {
        var frame = Frame(index);
        await using var loop = await Loopback.OpenAsync(Hex(frame["key"]), ulong.Parse(String(frame["counter"])) + 1);
        await loop.WriteFrameAsync(Hex(frame["payload"]));
        await Assert.ThrowsAsync<ProtocolException>(() => loop.Channel.ReceiveAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(FrameIndexes))]
    public async Task TamperedTagIsRejected(int index)
    {
        var frame = Frame(index);
        var payload = Hex(frame["payload"]);
        payload[^1] ^= 1;
        await using var loop = await Loopback.OpenAsync(Hex(frame["key"]), ulong.Parse(String(frame["counter"])));
        await loop.WriteFrameAsync(payload);
        await Assert.ThrowsAsync<ProtocolException>(() => loop.Channel.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CounterAdvancesWithEachFrame()
    {
        var key = Hex(Section("session")["k_ir"]);
        var ping = new JsonObject { ["t"] = "ping" };
        await using var fromZero = await Loopback.OpenAsync(key, 0);
        await fromZero.Channel.SendAsync(new JsonObject { ["t"] = "ping" }, CancellationToken.None);
        await fromZero.Channel.SendAsync(ping, CancellationToken.None);
        await fromZero.ReadFrameAsync();
        var second = await fromZero.ReadFrameAsync();

        await using var fromOne = await Loopback.OpenAsync(key, 1);
        await fromOne.Channel.SendAsync(new JsonObject { ["t"] = "ping" }, CancellationToken.None);
        Assert.Equal(ToHex(await fromOne.ReadFrameAsync()), ToHex(second));
    }
}

/// Кодировка сообщений. Байты у Swift и .NET разные (порядок ключей, экранирование эмодзи),
/// поэтому сообщения сравниваются как объекты JSON.
public class MessageTests
{
    public static TheoryData<string> MessageNames =>
        [.. Root["messages"]!.AsArray().Select(message => String(message!["name"]))];

    private static JsonObject Sample(string name) =>
        Root["messages"]!.AsArray().Single(message => String(message!["name"]) == name)!.AsObject();

    private static byte[] SampleBytes(string name) => Encoding.UTF8.GetBytes(String(Sample(name)["json"]));

    [Theory]
    [MemberData(nameof(MessageNames))]
    public void DecodeAndEncodeKeepObject(string name)
    {
        var decoded = Messages.Decode(SampleBytes(name));
        // Варианты с новыми полями записаны под своими именами, их t — в поле "type" образца.
        Assert.Equal(Sample(name)["type"]?.GetValue<string>() ?? name, Messages.Type(decoded));
        var encoded = Messages.Encode(decoded);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SampleBytes(name)), JsonNode.Parse(encoded)), Encoding.UTF8.GetString(encoded));

        var text = Encoding.UTF8.GetString(encoded);
        Assert.DoesNotContain(@"\/", text);
        // Кириллица и прочие символы BMP — как есть в UTF-8; \u допустим только для суррогатных пар (эмодзи).
        Assert.DoesNotMatch(@"\\u(?![dD][89a-fA-F])", text);
    }

    /// Сообщения в том виде, как их собирает ядро (Node.cs, Session.cs, Pairing.cs), совпадают с образцами как объекты.
    [Fact]
    public void ProductionShapes()
    {
        var clip = Sample("clip");
        var sampleClip = JsonNode.Parse(String(clip["json"]))!.AsObject();
        var built = new JsonObject
        {
            ["t"] = "clip",
            ["id"] = Messages.String(sampleClip, "id"),
            ["origin"] = Messages.String(sampleClip, "origin"),
            ["hops"] = sampleClip["hops"]!.GetValue<int>(),
            ["text"] = Messages.String(sampleClip, "text"),
        };
        Assert.True(JsonNode.DeepEquals(sampleClip, JsonNode.Parse(Messages.Encode(built))));

        var ready = new JsonObject { ["t"] = "ready", ["name"] = "Кухня / ноутбук" };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SampleBytes("ready")), JsonNode.Parse(Messages.Encode(ready))));

        var hello = new JsonObject
        {
            ["t"] = "hello",
            ["v"] = Protocol.Version,
            ["id"] = String(Section("device_id")["initiator"]),
            ["eph"] = Convert.ToBase64String(PublicKey("initiator_ephemeral")),
        };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SampleBytes("hello")), JsonNode.Parse(Messages.Encode(hello))));
    }

    /// Точные байты .NET для clip: записаны в vectors.json как foreign_messages, их разбирает Mac.
    [Fact]
    public void DotnetBytesForMac()
    {
        var foreign = Root["foreign_messages"]!.AsArray().Single(message => String(message!["name"]) == "clip_dotnet")!.AsObject();
        var sampleClip = JsonNode.Parse(String(Sample("clip")["json"]))!.AsObject();
        var built = new JsonObject
        {
            ["t"] = "clip",
            ["id"] = Messages.String(sampleClip, "id"),
            ["origin"] = Messages.String(sampleClip, "origin"),
            ["hops"] = sampleClip["hops"]!.GetValue<int>(),
            ["text"] = Messages.String(sampleClip, "text"),
        };
        Assert.Equal(String(foreign["json"]), Encoding.UTF8.GetString(Messages.Encode(built)));
        Assert.Equal(String(foreign["text"]), Messages.String(Messages.Decode(Encoding.UTF8.GetBytes(String(foreign["json"]))), "text"));
    }

    [Theory]
    [InlineData("{\"t\":\"pair_hello\",\"v\":1,\"name\":\"x\",\"key\":\"AAAA\"}", "key", 65)]
    [InlineData("{\"t\":\"pair_nonce\",\"nonce\":\"***\"}", "nonce", 32)]
    [InlineData("{\"t\":\"pair_nonce\"}", "nonce", 32)]
    public void BadFieldsAreRejected(string json, string field, int length)
    {
        var message = Messages.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Throws<ProtocolException>(() => Messages.Bytes(message, field, length));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("[1,2]")]
    public void NotAnObjectIsRejected(string json) =>
        Assert.Throws<ProtocolException>(() => Messages.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ErrorAndAbortBecomeRejection()
    {
        var error = Messages.Decode(SampleBytes("error"));
        var rejected = Assert.Throws<PeerRejectedException>(() => Messages.Expect(error, "hello_ack"));
        Assert.Equal("disabled", rejected.Reason);
        Assert.Throws<ProtocolException>(() => Messages.Expect(Messages.Decode(SampleBytes("ping")), "ready"));
    }
}

/// Новые необязательные поля (тип устройства, info, caps) и картинки: сообщения в том виде,
/// как их собирает и разбирает ядро, совпадают с образцами tests/vectors.json.
public class NewFieldTests
{
    private static JsonObject SampleMessage(string name) =>
        JsonNode.Parse(String(Root["messages"]!.AsArray().Single(message => String(message!["name"]) == name)!["json"]))!.AsObject();

    private static JsonObject Parse(string json) => Messages.Decode(Encoding.UTF8.GetBytes(json));

    private static void AssertSameObject(JsonObject expected, JsonObject actual) =>
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(Messages.Encode(actual))), Encoding.UTF8.GetString(Messages.Encode(actual)));

    [Fact]
    public void OldMessagesGiveDefaults()
    {
        var ready = PeerInfo.Parse(SampleMessage("ready"));
        Assert.Equal("Кухня / ноутбук", ready.Name);
        Assert.Null(ready.Caps);
        Assert.False(ready.AcceptsImages);
        Assert.Equal(DeviceType.Unknown, ready.Type);
        Assert.Equal(DeviceType.Unknown, DeviceType.Parse(SampleMessage("pair_hello")));
        Assert.Equal(DeviceType.Unknown, DeviceType.Parse(SampleMessage("pair_commit")));
        var empty = PeerInfo.Parse(Parse("""{"t":"info"}"""));
        Assert.Null(empty.Name);
        Assert.Null(empty.Caps);
        Assert.Equal(DeviceType.Unknown, empty.Type);
    }

    [Fact]
    public void ReadyAndInfoRoundTrip()
    {
        var full = PeerInfo.Parse(SampleMessage("ready_full"));
        Assert.Equal("OFFICE-PC", full.Name);
        Assert.Equal(new DeviceType("windows", "desktop"), full.Type);
        Assert.Equal(["image"], full.Caps!);
        Assert.True(full.AcceptsImages);
        AssertSameObject(SampleMessage("ready_full"), new PeerInfo("OFFICE-PC", new DeviceType("windows", "desktop"), ["image"]).ToMessage("ready"));
        AssertSameObject(SampleMessage("ready"), new PeerInfo("Кухня / ноутбук", DeviceType.Unknown, null).ToMessage("ready"));
        AssertSameObject(SampleMessage("info"), new PeerInfo("Кухня / ноутбук", new DeviceType("mac", "laptop"), ["image"]).ToMessage("info"));
        AssertSameObject(SampleMessage("info_name_only"), new PeerInfo("Новое имя", DeviceType.Unknown, null).ToMessage("info"));
        AssertSameObject(SampleMessage("info_caps_empty"), new PeerInfo(null, DeviceType.Unknown, []).ToMessage("info"));
        var capsEmpty = PeerInfo.Parse(SampleMessage("info_caps_empty"));
        Assert.NotNull(capsEmpty.Caps);
        Assert.Empty(capsEmpty.Caps);
        Assert.Null(capsEmpty.Name);
    }

    [Fact]
    public void WrongTypesOfOptionalFieldsAreIgnored()
    {
        var info = PeerInfo.Parse(Parse("""{"t":"ready","name":5,"os":true,"caps":["image",3]}"""));
        Assert.Null(info.Name);
        Assert.Null(info.Type.Os);
        Assert.Equal(["image"], info.Caps!);
    }

    [Fact]
    public void DeviceTypeInPairing()
    {
        Assert.Equal(new DeviceType("mac", "laptop"), DeviceType.Parse(SampleMessage("pair_hello_typed")));
        Assert.Equal(new DeviceType("windows", "desktop"), DeviceType.Parse(SampleMessage("pair_commit_typed")));
    }

    [Fact]
    public void BlobMessages()
    {
        var sample = SampleMessage("blob_start");
        var start = BlobStart.Parse(sample);
        Assert.Equal("image", start.Kind);
        Assert.Equal("image/png", start.Mime);
        Assert.Equal(1, start.Hops);
        Assert.Null(BlobAssembly.Refusal(start));
        AssertSameObject(sample, start.ToMessage());

        var chunk = SampleMessage("blob_chunk");
        var data = Blob.ChunkData(chunk)!;
        Assert.Equal("последний кусок 😀", Encoding.UTF8.GetString(data));
        AssertSameObject(chunk, Blob.ChunkMessage(Messages.String(chunk, "id"), 2, data));
        AssertSameObject(SampleMessage("blob_end"), Blob.EndMessage("ffeeddccbbaa99887766554433221100"));

        // Неверный blob_start не рвёт сеанс: разбирается, но картинка отвергается.
        var bad = BlobStart.Parse(Parse("""{"t":"blob_start","id":"a","origin":"b","hops":0,"kind":"file","mime":"image/gif","size":"big","sha256":"***"}"""));
        Assert.NotNull(BlobAssembly.Refusal(bad));
        Assert.Null(Blob.ChunkData(Parse("""{"t":"blob_chunk","id":"a","seq":0,"data":"***"}""")));
    }

    private static byte[] BlobData()
    {
        var length = Section("blob")["length"]!.GetValue<int>();
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)(i % 251);
        return data;
    }

    private static BlobStart Header(byte[] data, byte[]? sha = null) =>
        new("x", "o", 0, "image", "image/png", data.Length, sha ?? SHA256.HashData(data));

    [Fact]
    public void ChunkingMatchesVectors()
    {
        var blob = Section("blob");
        var data = BlobData();
        Assert.Equal(String(blob["sha256"]), Convert.ToBase64String(SHA256.HashData(data)));
        var chunks = Blob.Chunks(data).ToList();
        Assert.Equal(blob["chunk_sizes"]!.AsArray().Select(size => size!.GetValue<int>()), chunks.Select(chunk => chunk.Length));
        Assert.Equal(blob["chunk_sha256"]!.AsArray().Select(String), chunks.Select(chunk => ToHex(SHA256.HashData(chunk.Span))));
        Assert.Equal(524_288, Protocol.BlobChunkBytes);
        Assert.Equal(20_971_520, Protocol.MaxImageBytes);
    }

    [Fact]
    public void AssemblyAcceptsWholeImage()
    {
        var data = BlobData();
        var assembly = new BlobAssembly(Header(data));
        var seq = 0;
        foreach (var chunk in Blob.Chunks(data))
            assembly.Append(seq++, chunk.ToArray());
        Assert.Equal(data, assembly.Finish());
    }

    public static TheoryData<string> BadAssemblies => ["пропущен кусок", "нехватка данных", "короткий не последний", "данных больше size", "без данных", "sha256"];

    [Theory]
    [MemberData(nameof(BadAssemblies))]
    public void AssemblyRejects(string name)
    {
        var data = BlobData();
        var chunks = Blob.Chunks(data).Select(chunk => chunk.ToArray()).ToList();
        var assembly = new BlobAssembly(name == "sha256" ? Header(data, new byte[32]) : Header(data));
        Assert.Throws<ProtocolException>(() =>
        {
            switch (name)
            {
                case "пропущен кусок":
                    assembly.Append(0, chunks[0]);
                    assembly.Append(2, chunks[2]);
                    break;
                case "нехватка данных":
                    assembly.Append(0, chunks[0]);
                    assembly.Append(1, chunks[1]);
                    break;
                case "короткий не последний":
                    assembly.Append(0, chunks[0][..1000]);
                    break;
                case "данных больше size":
                    for (var i = 0; i < chunks.Count; i++)
                        assembly.Append(i, chunks[i]);
                    assembly.Append(chunks.Count, [1]);
                    break;
                case "без данных":
                    assembly.Append(0, null);
                    break;
                case "sha256":
                    for (var i = 0; i < chunks.Count; i++)
                        assembly.Append(i, chunks[i]);
                    break;
            }
            assembly.Finish();
        });
    }

    [Fact]
    public void RefusedHeaders()
    {
        var sha = new byte[32];
        Assert.NotNull(BlobAssembly.Refusal(new BlobStart("x", "o", 0, "file", "image/png", 10, sha)));
        Assert.NotNull(BlobAssembly.Refusal(new BlobStart("x", "o", 0, "image", "image/gif", 10, sha)));
        Assert.NotNull(BlobAssembly.Refusal(new BlobStart("x", "o", 0, "image", "image/png", 0, sha)));
        Assert.NotNull(BlobAssembly.Refusal(new BlobStart("x", "o", 0, "image", "image/jpeg", 20_971_521, sha)));
        Assert.NotNull(BlobAssembly.Refusal(new BlobStart("x", "o", 0, "image", "image/png", 10, new byte[31])));
        Assert.Null(BlobAssembly.Refusal(new BlobStart("x", "o", 0, "image", "image/jpeg", 20_971_520, sha)));
    }

    /// devices.json версии 0.1.0 (без Os, Form и Alias) читается; новые поля меняются по отдельности.
    [Fact]
    public void OldDevicesFileLoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clipvey-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "devices.json"),
                """[{"DeviceId":"ab","Name":"PC","PublicKey":"AA==","LastHost":"10.0.0.2","LastPort":48620,"Enabled":false}]""");
            var store = new DeviceStore(directory);
            var device = Assert.Single(store.Load());
            Assert.Equal("PC", device.Name);
            Assert.False(device.Enabled);
            Assert.Null(device.Os);
            Assert.Null(device.Alias);
            store.SetAlias("ab", "Кухня");
            store.UpdateInfo("ab", "PC-2", new DeviceType("windows", null));
            device = Assert.Single(store.Load());
            Assert.Equal("Кухня", device.Alias);
            Assert.Equal("PC-2", device.Name);
            Assert.Equal("windows", device.Os);
            Assert.Null(device.Form);
            store.UpdateInfo("ab", null, DeviceType.Unknown);
            Assert.Equal("PC-2", store.Load()[0].Name);
            Assert.Equal("windows", store.Load()[0].Os);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("  Кухня  ", "Кухня")]
    [InlineData("", "")]
    public void NameNormalization(string raw, string expected) => Assert.Equal(expected, ClipveyNode.NormalizeName(raw));

    [Fact]
    public void LongNameIsCutAt63Bytes()
    {
        var name = ClipveyNode.NormalizeName(new string('ж', 40));
        Assert.Equal(31, name.Length);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= 63);
    }

    [Fact]
    public void FailureCodes()
    {
        Assert.Equal(FailureReason.Disabled, Failures.Of(new PeerRejectedException("disabled")));
        Assert.Equal(FailureReason.UnknownDevice, Failures.Of(new PeerRejectedException("unknown_device")));
        Assert.Equal(FailureReason.Rejected, Failures.Of(new PeerRejectedException("что-то новое")));
        Assert.Equal(FailureReason.CodeMismatch, Failures.Of(new PairingCodeMismatchException()));
        Assert.Equal(FailureReason.WrongDevice, Failures.Of(new WrongDeviceException()));
        Assert.Equal(FailureReason.Cancelled, Failures.Of(new OperationCanceledException()));
        Assert.Equal(FailureReason.ConnectionFailed, Failures.Of(new SocketException()));
        Assert.Equal(FailureReason.ProtocolError, Failures.Of(new ProtocolException("x")));
    }
}

/// Подпись SHA256SUMS (docs/releases.md): подписано scripts/sign-release.swift одноразовым ключом,
/// проверяется так же, как будет проверять обновление на Windows.
public class ReleaseSignatureTests
{
    private static ECDsa ImportPublicKey(byte[] x963) => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = x963[1..33], Y = x963[33..65] },
    });

    [Fact]
    public void SwiftSignatureVerifiesInDotnet()
    {
        var release = Section("release_signature");
        using var key = ImportPublicKey(Convert.FromBase64String(String(release["public_key"])));
        var message = Encoding.UTF8.GetBytes(String(release["message"]));
        var signature = Convert.FromBase64String(String(release["signature"]));
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(message, signature, HashAlgorithmName.SHA256));
        byte[] changed = [.. message, (byte)'x'];
        Assert.False(key.VerifyData(changed, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void ReleasePublicKeyFromDocsImports()
    {
        // Ключ, зашитый в приложения (docs/releases.md).
        var x963 = Convert.FromBase64String("BH2yxPlYNbki9eTLDttU3YTv5qjUJ8Biz4ChVq7y7OdeOqkmmtYi5Zch3XAfrF0x4qSrNvvNOaFMqMONHERTJJs=");
        Assert.Equal(65, x963.Length);
        using var key = ImportPublicKey(x963);
        Assert.Equal(256, key.KeySize);
    }
}
