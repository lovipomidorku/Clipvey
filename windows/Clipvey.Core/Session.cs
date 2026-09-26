using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Установленный сеанс. Remote — ready другой стороны (имя, тип, caps).
/// InitiatorId нужен, чтобы решить, какой из двух одновременных сеансов оставить.
public sealed record SessionInfo(SecureChannel Channel, PairedDevice Peer, PeerInfo Remote, string InitiatorId)
{
    public string PeerName => string.IsNullOrEmpty(Remote.Name) ? Peer.Name : Remote.Name;
}

/// Рукопожатие сеанса по docs/protocol.md.
public static class Session
{
    /// Сторона I. Соединение уже установлено; при ошибке его закрывает вызывающий.
    public static async Task<SessionInfo> InitiateAsync(
        TcpClient tcp, Identity identity, PairedDevice peer, PeerInfo own, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Protocol.HandshakeTimeout);
        var ct = timeout.Token;
        var stream = tcp.GetStream();

        using var ephemeral = P256.Generate();
        var ephemeralPublic = P256.EncodePublicKey(ephemeral);
        await Pairing.Send(stream, new JsonObject
        {
            ["t"] = "hello",
            ["v"] = Protocol.Version,
            ["id"] = identity.DeviceId,
            ["eph"] = Convert.ToBase64String(ephemeralPublic),
        }, ct);

        var ack = Messages.Expect(await Pairing.Receive(stream, ct), "hello_ack");
        if (Messages.String(ack, "id") != peer.DeviceId)
            throw new WrongDeviceException();
        var peerEphemeral = Messages.Bytes(ack, "eph", P256.PublicKeyLength);

        using var peerStatic = P256.ImportPublicKey(peer.PublicKey);
        using var peerEphemeralKey = P256.ImportPublicKey(peerEphemeral);
        var dh1 = P256.Agree(ephemeral, peerEphemeralKey);
        var dh2 = P256.Agree(identity.Key, peerEphemeralKey);
        var dh3 = P256.Agree(ephemeral, peerStatic);
        var transcript = PairingMath.SessionTranscript(identity.PublicKey, peer.PublicKey, ephemeralPublic, peerEphemeral);
        var (toPeer, fromPeer) = PairingMath.SessionKeys(dh1, dh2, dh3, transcript);

        var channel = new SecureChannel(tcp, stream, toPeer, fromPeer);
        var remote = await ExchangeReadyAsync(channel, own, ct);
        return new SessionInfo(channel, peer, remote, identity.DeviceId);
    }

    /// Сторона R. hello уже прочитан. findPeer ищет связанное устройство по deviceId.
    public static async Task<SessionInfo> RespondAsync(
        TcpClient tcp,
        JsonObject hello,
        Identity identity,
        Func<string, PairedDevice?> findPeer,
        PeerInfo own,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Protocol.HandshakeTimeout);
        var ct = timeout.Token;
        var stream = tcp.GetStream();

        var peerId = Messages.String(hello, "id");
        var peer = findPeer(peerId);
        if (peer is null)
        {
            await Pairing.Send(stream, new JsonObject { ["t"] = "error", ["reason"] = "unknown_device" }, ct);
            throw new ProtocolException($"Подключилось несвязанное устройство {peerId}");
        }
        var peerEphemeral = Messages.Bytes(hello, "eph", P256.PublicKeyLength);

        using var ephemeral = P256.Generate();
        var ephemeralPublic = P256.EncodePublicKey(ephemeral);
        await Pairing.Send(stream, new JsonObject
        {
            ["t"] = "hello_ack",
            ["id"] = identity.DeviceId,
            ["eph"] = Convert.ToBase64String(ephemeralPublic),
        }, ct);

        using var peerStatic = P256.ImportPublicKey(peer.PublicKey);
        using var peerEphemeralKey = P256.ImportPublicKey(peerEphemeral);
        var dh1 = P256.Agree(ephemeral, peerEphemeralKey);
        var dh2 = P256.Agree(ephemeral, peerStatic);
        var dh3 = P256.Agree(identity.Key, peerEphemeralKey);
        var transcript = PairingMath.SessionTranscript(peer.PublicKey, identity.PublicKey, peerEphemeral, ephemeralPublic);
        var (fromPeer, toPeer) = PairingMath.SessionKeys(dh1, dh2, dh3, transcript);

        var channel = new SecureChannel(tcp, stream, toPeer, fromPeer);
        var remote = await ExchangeReadyAsync(channel, own, ct);
        return new SessionInfo(channel, peer, remote, peer.DeviceId);
    }

    /// Обмен ready подтверждает ключи. При ошибке канал (вместе с соединением) закрывается.
    private static async Task<PeerInfo> ExchangeReadyAsync(SecureChannel channel, PeerInfo own, CancellationToken ct)
    {
        try
        {
            await channel.SendAsync(own.ToMessage("ready"), ct);
            return PeerInfo.Parse(Messages.Expect(await channel.ReceiveAsync(ct), "ready"));
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }
}

public static class Net
{
    /// Подключиться к первому отвечающему адресу.
    public static async Task<(TcpClient Client, string Host, int Port)> ConnectAnyAsync(
        IEnumerable<(string Host, int Port)> endpoints, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var (host, port) in endpoints)
        {
            var tcp = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Protocol.ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(host, port, timeout.Token);
                return (tcp, host, port);
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                tcp.Dispose();
                lastError = e is SocketException ? e : new ProtocolException($"{host}:{port} не отвечает");
            }
        }
        throw lastError ?? new ProtocolException("Нет адресов для подключения");
    }
}
