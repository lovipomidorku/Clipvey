using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Clipvey.Tests;

/// Общие проверочные данные tests/vectors.json (генерирует Mac: swift run clipvey-checks --generate).
internal static class Vectors
{
    public static readonly JsonObject Root = Load();

    public static JsonObject Section(string name) => Root[name]!.AsObject();

    public static string String(JsonNode? node) => node!.GetValue<string>();

    public static byte[] Hex(JsonNode? node) => Convert.FromHexString(String(node));

    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static byte[] PublicKey(string name) => Hex(Section("keys")[name]!["public"]);

    /// Закрытый ключ вместе с открытым: так импорт не зависит от того, умеет ли платформа вычислить Q из D.
    public static ECDiffieHellman PrivateKey(string name)
    {
        var publicKey = PublicKey(name);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Hex(Section("keys")[name]!["private"]),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
    }

    /// tests/vectors.json ищется вверх от папки сборки (bin/Debug/net10.0 → корень репозитория).
    private static JsonObject Load()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "vectors.json");
            if (File.Exists(candidate))
                return JsonNode.Parse(File.ReadAllBytes(candidate))!.AsObject();
        }
        throw new FileNotFoundException("Не найден tests/vectors.json выше " + AppContext.BaseDirectory);
    }
}
