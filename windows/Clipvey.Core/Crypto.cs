using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Clipvey.Core;

/// P-256: публичные ключи в формате X9.63 без сжатия (65 байт) и «сырой» ECDH.
public static class P256
{
    public const int PublicKeyLength = 65;

    public static ECDiffieHellman Generate() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] EncodePublicKey(ECDiffieHellman key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var encoded = new byte[PublicKeyLength];
        encoded[0] = 0x04;
        WritePadded(point.X!, encoded.AsSpan(1, 32));
        WritePadded(point.Y!, encoded.AsSpan(33, 32));
        return encoded;
    }

    /// Импорт с проверкой, что точка лежит на кривой. Объект нужно держать, пока идёт ECDH.
    public static ECDiffieHellman ImportPublicKey(byte[] encoded)
    {
        if (encoded.Length != PublicKeyLength || encoded[0] != 0x04)
            throw new ProtocolException("Неверный формат публичного ключа");
        try
        {
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = encoded[1..33], Y = encoded[33..65] },
            });
        }
        catch (CryptographicException)
        {
            throw new ProtocolException("Публичный ключ не лежит на кривой P-256");
        }
    }

    public static byte[] Agree(ECDiffieHellman privateKey, ECDiffieHellman publicKey)
    {
        using var peer = publicKey.PublicKey;
        return privateKey.DeriveRawSecretAgreement(peer);
    }

    private static void WritePadded(byte[] source, Span<byte> destination)
    {
        if (source.Length > destination.Length)
            throw new ProtocolException("Слишком длинная координата ключа");
        destination.Clear();
        source.CopyTo(destination[(destination.Length - source.Length)..]);
    }
}

/// Вычисления связывания и сеанса. Должны совпадать до байта с реализацией на Swift.
public static class PairingMath
{
    private static readonly byte[] CommitLabel = "Clipvey v1 commit"u8.ToArray();
    private static readonly byte[] CodeLabel = "Clipvey v1 code"u8.ToArray();
    private static readonly byte[] SessionLabel = "Clipvey v1 session"u8.ToArray();
    private static readonly byte[] KeysLabel = "Clipvey v1 keys"u8.ToArray();

    public static byte[] Commitment(byte[] responderKey, byte[] initiatorKey, byte[] responderNonce) =>
        SHA256.HashData(Concat(CommitLabel, responderKey, initiatorKey, responderNonce));

    public static string Code(byte[] initiatorKey, byte[] responderKey, byte[] initiatorNonce, byte[] responderNonce)
    {
        var hash = SHA256.HashData(Concat(CodeLabel, initiatorKey, responderKey, initiatorNonce, responderNonce));
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    public static byte[] SessionTranscript(byte[] initiatorKey, byte[] responderKey, byte[] initiatorEphemeral, byte[] responderEphemeral) =>
        SHA256.HashData(Concat(SessionLabel, initiatorKey, responderKey, initiatorEphemeral, responderEphemeral));

    public static (byte[] InitiatorToResponder, byte[] ResponderToInitiator) SessionKeys(byte[] dh1, byte[] dh2, byte[] dh3, byte[] transcript)
    {
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, Concat(dh1, dh2, dh3), 64, transcript, KeysLabel);
        return (okm[..32], okm[32..]);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}

/// Долговременный ключ этого устройства.
public sealed class Identity : IDisposable
{
    private Identity(ECDiffieHellman key)
    {
        Key = key;
        PublicKey = P256.EncodePublicKey(key);
        DeviceId = DeviceIdFor(PublicKey);
    }

    public ECDiffieHellman Key { get; }
    public byte[] PublicKey { get; }
    public string DeviceId { get; }

    public static Identity LoadOrCreate(ISecretStore store)
    {
        if (store.Load("identity") is { } stored)
        {
            var key = ECDiffieHellman.Create();
            key.ImportPkcs8PrivateKey(stored, out _);
            return new Identity(key);
        }
        var created = P256.Generate();
        store.Save("identity", created.ExportPkcs8PrivateKey());
        return new Identity(created);
    }

    public static string DeviceIdFor(byte[] publicKey) =>
        Convert.ToHexString(SHA256.HashData(publicKey).AsSpan(0, 16)).ToLowerInvariant();

    public void Dispose() => Key.Dispose();
}

public interface ISecretStore
{
    byte[]? Load(string name);
    void Save(string name, byte[] data);
}

/// Секреты в файлах, доступных только владельцу. Для Mac/Linux (проверки); на Windows — DPAPI.
public sealed class FileSecretStore(string directory) : ISecretStore
{
    public byte[]? Load(string name)
    {
        var path = Path.Combine(directory, name + ".key");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Save(string name, byte[] data)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".key");
        File.WriteAllBytes(path, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
