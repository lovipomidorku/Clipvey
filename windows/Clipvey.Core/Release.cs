using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

// Проверка обновлений (docs/releases.md): версия, ответ releases/latest, SHA256SUMS и его подпись,
// скачивание с проверкой. Без WinForms — проверяется в Clipvey.Tests. Установку делает Clipvey.Windows/Updater.cs.

/// Версия вида major.minor.patch (тег релиза — с «v» впереди).
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// «1.2.3» или «v1.2.3». Всё остальное (пре-релизы, лишние части) — null.
    public static ReleaseVersion? Parse(string? text)
    {
        if (text is null)
            return null;
        text = text.Trim();
        if (text.StartsWith('v'))
            text = text[1..];
        var parts = text.Split('.');
        if (parts.Length != 3)
            return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length is 0 or > 9 || !parts[i].All(char.IsAsciiDigit))
                return null;
            numbers[i] = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
    }

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// Почему не удалось: от этого зависит сообщение пользователю.
public enum ReleaseFailure
{
    /// Ответ сервера не разобран.
    BadResponse,

    /// Файл не скачался (сеть, код HTTP, не-HTTPS адрес).
    Download,

    /// Подпись SHA256SUMS или SHA-256 файла не совпали.
    Verification,
}

public sealed class ReleaseException(ReleaseFailure failure, string message) : Exception(message)
{
    public ReleaseFailure Failure { get; } = failure;
}

/// Нужное из ответа GitHub releases/latest: тег и файлы релиза (имя → адрес).
public sealed record ReleaseInfo(string Tag, ReleaseVersion Version, IReadOnlyDictionary<string, Uri> Assets)
{
    public static ReleaseInfo Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new ReleaseException(ReleaseFailure.BadResponse, $"ответ — не JSON: {e.Message}");
        }
        if (root is null)
            throw new ReleaseException(ReleaseFailure.BadResponse, "ответ — не объект JSON");
        var tag = StringOf(root["tag_name"]) ?? throw new ReleaseException(ReleaseFailure.BadResponse, "нет tag_name");
        var version = ReleaseVersion.Parse(tag)
            ?? throw new ReleaseException(ReleaseFailure.BadResponse, $"тег «{tag}» — не версия major.minor.patch");
        var assets = new Dictionary<string, Uri>();
        if (root["assets"] is JsonArray list)
        {
            foreach (var asset in list.OfType<JsonObject>())
            {
                if (StringOf(asset["name"]) is { } name
                    && StringOf(asset["browser_download_url"]) is { } link
                    && Uri.TryCreate(link, UriKind.Absolute, out var uri))
                    assets[name] = uri;
            }
        }
        return new ReleaseInfo(tag, version, assets);
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public static class ReleaseVerifier
{
    /// Открытый ключ подписи релизов, зашитый в приложение (X9.63, base64).
    public const string PublicKeyBase64 = "BH2yxPlYNbki9eTLDttU3YTv5qjUJ8Biz4ChVq7y7OdeOqkmmtYi5Zch3XAfrF0x4qSrNvvNOaFMqMONHERTJJs=";

    /// Проверяет подпись SHA256SUMS и возвращает его строки: имя файла → sha256 (hex строчными).
    /// signatureFile — содержимое SHA256SUMS.sig: base64 от 64 байт r‖s (пробелы по краям допустимы).
    public static IReadOnlyDictionary<string, string> VerifiedChecksums(byte[] sums, byte[] signatureFile, string publicKeyBase64 = PublicKeyBase64)
    {
        byte[] key;
        byte[] signature;
        try
        {
            key = Convert.FromBase64String(publicKeyBase64.Trim());
            signature = Convert.FromBase64String(Encoding.UTF8.GetString(signatureFile).Trim());
        }
        catch (FormatException)
        {
            throw new ReleaseException(ReleaseFailure.Verification, "ключ или подпись — не base64");
        }
        if (key.Length != 65 || key[0] != 0x04 || signature.Length != 64)
            throw new ReleaseException(ReleaseFailure.Verification, "неверная длина ключа или подписи");
        bool valid;
        try
        {
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = key[1..33], Y = key[33..65] },
            });
            // Формат подписи по умолчанию — IEEE P1363, то есть r‖s.
            valid = ecdsa.VerifyData(sums, signature, HashAlgorithmName.SHA256);
        }
        // Windows (CNG) на ключ не на кривой бросает PlatformNotSupportedException.
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            valid = false;
        }
        if (!valid)
            throw new ReleaseException(ReleaseFailure.Verification, "подпись SHA256SUMS не совпадает");
        return ParseChecksums(sums);
    }

    /// Строки «<sha256 hex строчными>  <имя>\n». Любое отступление от формата — ошибка.
    public static IReadOnlyDictionary<string, string> ParseChecksums(byte[] data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new ReleaseException(ReleaseFailure.Verification, "SHA256SUMS не в UTF-8");
        }
        if (!text.EndsWith('\n'))
            throw new ReleaseException(ReleaseFailure.Verification, "SHA256SUMS: нет перевода строки в конце");
        var result = new Dictionary<string, string>();
        foreach (var line in text[..^1].Split('\n'))
        {
            if (line.Length <= 66 || line[64] != ' ' || line[65] != ' '
                || !line[..64].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
                throw new ReleaseException(ReleaseFailure.Verification, $"SHA256SUMS: строка «{line}»");
            var name = line[66..];
            if (!result.TryAdd(name, line[..64]))
                throw new ReleaseException(ReleaseFailure.Verification, $"SHA256SUMS: {name} дважды");
        }
        return result;
    }

    /// SHA-256 файла совпадает со строкой из SHA256SUMS.
    public static void VerifyFile(string path, string name, IReadOnlyDictionary<string, string> checksums)
    {
        if (!checksums.TryGetValue(name, out var expected))
            throw new ReleaseException(ReleaseFailure.Verification, $"в SHA256SUMS нет строки для {name}");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expected)
            throw new ReleaseException(ReleaseFailure.Verification, $"SHA-256 файла {name} не совпадает с SHA256SUMS");
    }
}

public enum UpdateCheckStatus
{
    Newer,
    UpToDate,
    /// 404: релизов пока нет.
    NoReleases,
    /// Лимит запросов GitHub.
    RateLimited,
    /// Нет сети, таймаут, неожиданный ответ.
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release, string Detail);

/// Запросы обновлений. Только HTTPS (HttpClient и сам не идёт по перенаправлению с HTTPS на HTTP);
/// в режиме проверки допускается http://127.0.0.1 — для локального сервера.
public sealed class ReleaseClient(HttpClient http, bool allowLoopbackHttp)
{
    public const string DefaultUrl = "https://api.github.com/repos/lovipomidorku/Clipvey/releases/latest";
    public const string SumsName = "SHA256SUMS";
    public const string SignatureName = "SHA256SUMS.sig";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    /// HttpClient с User-Agent «Clipvey/версия»; таймаут общий на скачивание (exe — десятки мегабайт).
    public static HttpClient CreateHttpClient(string version)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Clipvey/{version}");
        return client;
    }

    public bool IsAllowed(Uri? uri) =>
        uri is not null && (uri.Scheme == Uri.UriSchemeHttps
            || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public async Task<UpdateCheckResult> CheckAsync(Uri url, ReleaseVersion current, CancellationToken cancel = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(CheckTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await SendAsync(request, timeout.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var release = ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    return release.Version > current
                        ? new UpdateCheckResult(UpdateCheckStatus.Newer, release, $"доступна {release.Version}")
                        : new UpdateCheckResult(UpdateCheckStatus.UpToDate, release, $"последняя версия {release.Version}, установлена {current}");
                case HttpStatusCode.NotFound:
                    return new UpdateCheckResult(UpdateCheckStatus.NoReleases, null, "релизов нет (404)");
                case HttpStatusCode.TooManyRequests:
                case HttpStatusCode.Forbidden when response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0":
                    return new UpdateCheckResult(UpdateCheckStatus.RateLimited, null, "лимит запросов GitHub");
                default:
                    return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (ReleaseException e)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"нет связи с сервером: {e.Message}");
        }
    }

    /// Скачать SHA256SUMS, подпись и файл assetName в папку directory и проверить их.
    /// Возвращает путь к проверенному файлу. При любой ошибке скачанное удаляется, бросается ReleaseException.
    public async Task<string> DownloadVerifiedAsync(ReleaseInfo release, string assetName, string directory, string publicKeyBase64, CancellationToken cancel = default)
    {
        if (!release.Assets.TryGetValue(assetName, out var fileUrl)
            || !release.Assets.TryGetValue(SumsName, out var sumsUrl)
            || !release.Assets.TryGetValue(SignatureName, out var signatureUrl))
            throw new ReleaseException(ReleaseFailure.Download, $"в релизе {release.Tag} нет {assetName}, {SumsName} или {SignatureName}");

        var path = Path.Combine(directory, assetName);
        try
        {
            Directory.CreateDirectory(directory);
            var sums = await DownloadBytesAsync(sumsUrl, cancel);
            var signature = await DownloadBytesAsync(signatureUrl, cancel);
            // Подпись проверяем до скачивания большого файла: с неверной подписью он не нужен.
            var checksums = ReleaseVerifier.VerifiedChecksums(sums, signature, publicKeyBase64);
            await DownloadFileAsync(fileUrl, path, cancel);
            ReleaseVerifier.VerifyFile(path, assetName, checksums);
            return path;
        }
        catch (Exception e)
        {
            TryDelete(path);
            if (e is ReleaseException)
                throw;
            throw new ReleaseException(ReleaseFailure.Download, $"скачивание: {e.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        if (!IsAllowed(request.RequestUri))
            throw new ReleaseException(ReleaseFailure.Download, $"адрес не HTTPS: {request.RequestUri}");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        // После перенаправлений RequestUri — конечный адрес.
        if (!IsAllowed(response.RequestMessage?.RequestUri ?? request.RequestUri))
        {
            response.Dispose();
            throw new ReleaseException(ReleaseFailure.Download, $"перенаправление не на HTTPS: {response.RequestMessage?.RequestUri}");
        }
        return response;
    }

    private async Task<byte[]> DownloadBytesAsync(Uri url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancel);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReleaseException(ReleaseFailure.Download, $"HTTP {(int)response.StatusCode}: {url}");
        return await response.Content.ReadAsByteArrayAsync(cancel);
    }

    private async Task DownloadFileAsync(Uri url, string path, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancel);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReleaseException(ReleaseFailure.Download, $"HTTP {(int)response.StatusCode}: {url}");
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancel);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
