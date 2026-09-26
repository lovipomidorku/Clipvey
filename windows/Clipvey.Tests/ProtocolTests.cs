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
        Assert.Equal(name, Messages.Type(decoded));
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
