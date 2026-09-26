using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Type — os и form, которые устройство сообщило при связывании (или Unknown).
public sealed record PairedDevice(string DeviceId, string Name, byte[] PublicKey, DeviceType? Type = null);

/// Входящее связывание (сторона R): код, подтверждение другой стороны и решение пользователя.
public sealed class IncomingPairing(string peerName, DeviceType peerType)
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string PeerName { get; } = peerName;

    /// os и form другого устройства из pair_hello (для значка).
    public DeviceType PeerType { get; } = peerType;

    /// Код для показа пользователю (null — ещё не вычислен).
    public string? Code { get; internal set; }

    /// Другое устройство подтвердило, что введённый код совпал. Теперь можно нажать «Готово».
    public bool Verified { get; internal set; }

    public void Confirm() => _decision.TrySetResult(true);

    public void Cancel() => _decision.TrySetResult(false);

    internal Task<bool> Decision => _decision.Task;
}

/// Шаги связывания по docs/protocol.md.
public static class Pairing
{
    /// Сторона I: пользователь вводит код с экрана другого устройства.
    /// <param name="requestCode">Спросить код (аргумент — имя другого устройства). null — отмена.</param>
    /// <param name="codeAccepted">Код совпал; осталось нажать «Готово» на другом устройстве.</param>
    public static async Task<PairedDevice> InitiateAsync(
        Stream stream,
        Identity identity,
        string ownName,
        DeviceType ownType,
        Func<string, CancellationToken, Task<string?>> requestCode,
        Action? codeAccepted,
        CancellationToken ct)
    {
        var hello = new JsonObject
        {
            ["t"] = "pair_hello",
            ["v"] = Protocol.Version,
            ["name"] = ownName,
            ["key"] = Convert.ToBase64String(identity.PublicKey),
        };
        ownType.WriteTo(hello);
        await Send(stream, hello, ct);

        var commitMessage = Messages.Expect(await Receive(stream, ct), "pair_commit");
        var peerName = Messages.String(commitMessage, "name");
        var peerKey = Messages.Bytes(commitMessage, "key", P256.PublicKeyLength);
        var commitment = Messages.Bytes(commitMessage, "commit", 32);
        var peerType = DeviceType.Parse(commitMessage);
        using (P256.ImportPublicKey(peerKey))
        {
            // Только проверка, что ключ — точка на кривой.
        }

        var ownNonce = RandomNumberGenerator.GetBytes(32);
        await Send(stream, new JsonObject { ["t"] = "pair_nonce", ["nonce"] = Convert.ToBase64String(ownNonce) }, ct);

        var reveal = Messages.Expect(await Receive(stream, ct), "pair_reveal");
        var peerNonce = Messages.Bytes(reveal, "nonce", 32);
        if (!CryptographicOperations.FixedTimeEquals(PairingMath.Commitment(peerKey, identity.PublicKey, peerNonce), commitment))
        {
            await TrySendAbort(stream, "commit");
            throw new CommitMismatchException();
        }

        var code = PairingMath.Code(identity.PublicKey, peerKey, ownNonce, peerNonce);
        Log.Write($"Связывание: ждём ввода кода с экрана «{peerName}»");
        var typed = await requestCode(peerName, ct);
        if (typed is null)
        {
            await TrySendAbort(stream, "cancel");
            throw new OperationCanceledException("Связывание отменено");
        }
        if (new string(typed.Where(char.IsAsciiDigit).ToArray()) != code)
        {
            await TrySendAbort(stream, "code");
            throw new PairingCodeMismatchException();
        }

        await Send(stream, new JsonObject { ["t"] = "pair_verified" }, ct);
        codeAccepted?.Invoke();
        Messages.Expect(await Receive(stream, ct), "pair_done");
        Log.Write($"Связывание с «{peerName}» завершено");
        return new PairedDevice(Identity.DeviceIdFor(peerKey), peerName, peerKey, peerType);
    }

    /// Сторона R: показываем код, ждём подтверждения другой стороны и нажатия «Готово».
    public static async Task<PairedDevice> RespondAsync(
        Stream stream,
        JsonObject hello,
        Identity identity,
        string ownName,
        DeviceType ownType,
        IncomingPairing pairing,
        Action changed,
        CancellationToken cancellationToken)
    {
        // Отмена пользователем прерывает и ожидание сообщений от другой стороны.
        using var userCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = pairing.Decision.ContinueWith(decision =>
        {
            if (!decision.Result)
                userCancel.Cancel();
        }, TaskScheduler.Default);
        var ct = userCancel.Token;

        try
        {
            var peerKey = Messages.Bytes(hello, "key", P256.PublicKeyLength);
            using (P256.ImportPublicKey(peerKey))
            {
                // Только проверка, что ключ — точка на кривой.
            }

            var ownNonce = RandomNumberGenerator.GetBytes(32);
            var commit = new JsonObject
            {
                ["t"] = "pair_commit",
                ["name"] = ownName,
                ["key"] = Convert.ToBase64String(identity.PublicKey),
                ["commit"] = Convert.ToBase64String(PairingMath.Commitment(identity.PublicKey, peerKey, ownNonce)),
            };
            ownType.WriteTo(commit);
            await Send(stream, commit, ct);

            var nonceMessage = Messages.Expect(await Receive(stream, ct), "pair_nonce");
            var peerNonce = Messages.Bytes(nonceMessage, "nonce", 32);
            await Send(stream, new JsonObject { ["t"] = "pair_reveal", ["nonce"] = Convert.ToBase64String(ownNonce) }, ct);

            pairing.Code = PairingMath.Code(peerKey, identity.PublicKey, peerNonce, ownNonce);
            changed();

            Messages.Expect(await Receive(stream, ct), "pair_verified");
            pairing.Verified = true;
            changed();

            if (!await pairing.Decision.WaitAsync(ct))
                throw new OperationCanceledException("Связывание отменено");
            await Send(stream, new JsonObject { ["t"] = "pair_done" }, ct);
            Log.Write($"Связывание с «{pairing.PeerName}» завершено");
            return new PairedDevice(Identity.DeviceIdFor(peerKey), pairing.PeerName, peerKey, pairing.PeerType);
        }
        catch (OperationCanceledException)
        {
            await TrySendAbort(stream, "cancel");
            throw;
        }
    }

    internal static Task Send(Stream stream, JsonObject message, CancellationToken ct) =>
        Frames.WriteAsync(stream, Messages.Encode(message), ct);

    internal static async Task<JsonObject> Receive(Stream stream, CancellationToken ct) =>
        Messages.Decode(await Frames.ReadAsync(stream, ct));

    private static async Task TrySendAbort(Stream stream, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Send(stream, new JsonObject { ["t"] = "pair_abort", ["reason"] = reason }, timeout.Token);
        }
        catch (Exception)
        {
            // Соединение всё равно закрывается.
        }
    }
}
