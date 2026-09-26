using System.Net;
using System.Security.Cryptography;
using System.Text;
using Clipvey.Core;
using static Clipvey.Tests.Vectors;

namespace Clipvey.Tests;

/// Проверка обновлений (docs/releases.md): версии, releases/latest, SHA256SUMS, подпись, скачивание.
public class ReleaseTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("10.0.1", "10.0.1")]
    public void VersionParses(string text, string expected) => Assert.Equal(expected, ReleaseVersion.Parse(text)?.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.x")]
    [InlineData("1.2.3-beta")]
    [InlineData("1..3")]
    [InlineData("+1.2.3")]
    [InlineData("１.2.3")]
    [InlineData("latest")]
    public void BadVersionRejected(string text) => Assert.Null(ReleaseVersion.Parse(text));

    [Fact]
    public void VersionsCompareAsNumbers()
    {
        string[] ordered = ["0.1.0", "0.1.1", "0.2.0", "0.10.0", "1.0.0", "1.0.10"];
        var versions = ordered.Select(text => ReleaseVersion.Parse(text)!.Value).ToList();
        Assert.Equal(versions, versions.Order().ToList());
        Assert.True(ReleaseVersion.Parse("0.10.0") > ReleaseVersion.Parse("0.9.9"));
        Assert.False(ReleaseVersion.Parse("v0.1.0") > ReleaseVersion.Parse("0.1.0"));
    }

    private const string LatestJson = """
        {"tag_name":"v0.2.0","draft":false,"assets":[
          {"name":"Clipvey.exe","browser_download_url":"https://example.com/d/Clipvey.exe","size":1},
          {"name":"SHA256SUMS","browser_download_url":"https://example.com/d/SHA256SUMS"},
          {"name":"SHA256SUMS.sig","browser_download_url":"https://example.com/d/SHA256SUMS.sig"},
          {"name":"broken"}],
         "extra":{"x":1}}
        """;

    [Fact]
    public void LatestJsonParses()
    {
        var release = ReleaseInfo.Parse(LatestJson);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("0.2.0", release.Version.ToString());
        Assert.Equal(["Clipvey.exe", "SHA256SUMS", "SHA256SUMS.sig"], release.Assets.Keys.Order());
        Assert.Equal("https://example.com/d/Clipvey.exe", release.Assets["Clipvey.exe"].ToString());
    }

    [Theory]
    [InlineData("""{"assets":[]}""")]
    [InlineData("""{"tag_name":"latest"}""")]
    [InlineData("""{"tag_name":5}""")]
    [InlineData("[]")]
    [InlineData("не json")]
    public void BadLatestJsonRejected(string json)
    {
        var error = Assert.Throws<ReleaseException>(() => ReleaseInfo.Parse(json));
        Assert.Equal(ReleaseFailure.BadResponse, error.Failure);
    }

    [Fact]
    public void SwiftSignedChecksumsVerify()
    {
        // Подписано scripts/sign-release.swift одноразовым ключом (tests/vectors.json).
        var release = Section("release_signature");
        var message = Encoding.UTF8.GetBytes(String(release["message"]));
        var signature = Encoding.UTF8.GetBytes(String(release["signature"]) + "\n");
        var key = String(release["public_key"]);
        var sums = ReleaseVerifier.VerifiedChecksums(message, signature, key);
        Assert.Equal("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210", sums["Clipvey.exe"]);

        AssertVerificationFails(() => ReleaseVerifier.VerifiedChecksums([.. message, (byte)'x'], signature, key));
        // Зашитый ключ релизов не подходит к одноразовой подписи.
        AssertVerificationFails(() => ReleaseVerifier.VerifiedChecksums(message, signature));
        var tampered = Convert.FromBase64String(String(release["signature"]));
        tampered[5] ^= 1;
        AssertVerificationFails(() => ReleaseVerifier.VerifiedChecksums(message, Encoding.UTF8.GetBytes(Convert.ToBase64String(tampered)), key));
        AssertVerificationFails(() => ReleaseVerifier.VerifiedChecksums(message, "***"u8.ToArray(), key));
        AssertVerificationFails(() => ReleaseVerifier.VerifiedChecksums(message, signature, "AAAA"));
    }

    [Theory]
    [InlineData("{0}  a")]
    [InlineData("{0} a\n")]
    [InlineData("{1}  a\n")]
    [InlineData("{2}  a\n")]
    [InlineData("{0}  a\n{0}  a\n")]
    [InlineData("{0}  \n")]
    public void BadChecksumsRejected(string template)
    {
        var hash = new string('a', 64);
        var text = string.Format(template, hash, hash.ToUpperInvariant(), hash[..63]);
        AssertVerificationFails(() => ReleaseVerifier.ParseChecksums(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void FileHashIsChecked()
    {
        var directory = Directory.CreateTempSubdirectory("clipvey-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "Clipvey.exe");
            File.WriteAllText(path, "новая версия");
            var sums = ReleaseVerifier.ParseChecksums(Encoding.UTF8.GetBytes($"{Sha256("новая версия")}  Clipvey.exe\n"));
            ReleaseVerifier.VerifyFile(path, "Clipvey.exe", sums);
            AssertVerificationFails(() => ReleaseVerifier.VerifyFile(path, "Clipvey-mac.zip", sums));
            File.AppendAllText(path, "!");
            AssertVerificationFails(() => ReleaseVerifier.VerifyFile(path, "Clipvey.exe", sums));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // MARK: - Сеть (через подставной HttpMessageHandler)

    private static readonly ReleaseVersion Current = new(0, 1, 0);

    [Fact]
    public async Task CheckFindsNewerVersion()
    {
        var server = new FakeServer { ["https://api.test/latest"] = FakeServer.Ok(LatestJson) };
        var result = await Client(server).CheckAsync(new Uri("https://api.test/latest"), Current);
        Assert.Equal(UpdateCheckStatus.Newer, result.Status);
        Assert.Equal("0.2.0", result.Release!.Version.ToString());
        var request = server.Requests.Single();
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Equal("Clipvey/0.1.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CheckStatuses()
    {
        var server = new FakeServer
        {
            ["https://api.test/same"] = FakeServer.Ok("""{"tag_name":"v0.1.0","assets":[]}"""),
            ["https://api.test/older"] = FakeServer.Ok("""{"tag_name":"v0.0.9","assets":[]}"""),
            ["https://api.test/missing"] = () => new HttpResponseMessage(HttpStatusCode.NotFound),
            ["https://api.test/limit"] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.Add("X-RateLimit-Remaining", "0");
                return response;
            },
            ["https://api.test/busy"] = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            ["https://api.test/broken"] = FakeServer.Ok("<html>"),
            ["https://api.test/error"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            ["https://api.test/offline"] = () => throw new HttpRequestException("нет сети"),
        };
        var client = Client(server);
        async Task<UpdateCheckStatus> Status(string path) => (await client.CheckAsync(new Uri("https://api.test/" + path), Current)).Status;
        Assert.Equal(UpdateCheckStatus.UpToDate, await Status("same"));
        Assert.Equal(UpdateCheckStatus.UpToDate, await Status("older"));
        Assert.Equal(UpdateCheckStatus.NoReleases, await Status("missing"));
        Assert.Equal(UpdateCheckStatus.RateLimited, await Status("limit"));
        Assert.Equal(UpdateCheckStatus.RateLimited, await Status("busy"));
        Assert.Equal(UpdateCheckStatus.Failed, await Status("broken"));
        Assert.Equal(UpdateCheckStatus.Failed, await Status("error"));
        Assert.Equal(UpdateCheckStatus.Failed, await Status("offline"));
    }

    [Fact]
    public async Task OnlyHttpsUnlessLoopbackInTestMode()
    {
        var server = new FakeServer
        {
            ["http://api.test/latest"] = FakeServer.Ok(LatestJson),
            ["http://127.0.0.1:8765/latest"] = FakeServer.Ok(LatestJson),
        };
        Assert.Equal(UpdateCheckStatus.Failed, (await Client(server).CheckAsync(new Uri("http://api.test/latest"), Current)).Status);
        Assert.Equal(UpdateCheckStatus.Failed, (await Client(server).CheckAsync(new Uri("http://127.0.0.1:8765/latest"), Current)).Status);
        Assert.Empty(server.Requests);
        var testClient = new ReleaseClient(new HttpClient(server), allowLoopbackHttp: true);
        Assert.Equal(UpdateCheckStatus.Failed, (await testClient.CheckAsync(new Uri("http://api.test/latest"), Current)).Status);
        Assert.Equal(UpdateCheckStatus.Newer, (await testClient.CheckAsync(new Uri("http://127.0.0.1:8765/latest"), Current)).Status);
    }

    [Fact]
    public async Task RedirectToHttpIsRejected()
    {
        var server = new FakeServer { ["https://api.test/latest"] = FakeServer.Ok(LatestJson, finalUrl: "http://evil.test/latest") };
        Assert.Equal(UpdateCheckStatus.Failed, (await Client(server).CheckAsync(new Uri("https://api.test/latest"), Current)).Status);
    }

    [Fact]
    public async Task DownloadVerifiesSignatureAndHash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = PublicKeyBase64(key);
        var exe = "MZ новый exe"u8.ToArray();

        var directory = Directory.CreateTempSubdirectory("clipvey-tests-").FullName;
        try
        {
            // Всё верно.
            var good = Release(key, exe, exe);
            var path = await Client(good.Server).DownloadVerifiedAsync(good.Info, "Clipvey.exe", Path.Combine(directory, "good"), publicKey);
            Assert.Equal(exe, File.ReadAllBytes(path));

            // Файл подменён после подписи.
            var badHash = Release(key, exe, [.. exe, 0]);
            var target = Path.Combine(directory, "hash");
            var error = await Assert.ThrowsAsync<ReleaseException>(() =>
                Client(badHash.Server).DownloadVerifiedAsync(badHash.Info, "Clipvey.exe", target, publicKey));
            Assert.Equal(ReleaseFailure.Verification, error.Failure);
            Assert.False(File.Exists(Path.Combine(target, "Clipvey.exe")));

            // Подписано другим ключом: exe даже не скачивается.
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var badSignature = Release(otherKey, exe, exe);
            error = await Assert.ThrowsAsync<ReleaseException>(() =>
                Client(badSignature.Server).DownloadVerifiedAsync(badSignature.Info, "Clipvey.exe", Path.Combine(directory, "sig"), publicKey));
            Assert.Equal(ReleaseFailure.Verification, error.Failure);
            Assert.DoesNotContain(badSignature.Server.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("Clipvey.exe"));

            // Файла нет в релизе.
            error = await Assert.ThrowsAsync<ReleaseException>(() =>
                Client(good.Server).DownloadVerifiedAsync(good.Info, "Clipvey-mac.zip", Path.Combine(directory, "none"), publicKey));
            Assert.Equal(ReleaseFailure.Download, error.Failure);

            // Сервер ответил ошибкой на скачивание файла.
            var missing = Release(key, exe, exe);
            missing.Server["https://dl.test/Clipvey.exe"] = () => new HttpResponseMessage(HttpStatusCode.NotFound);
            error = await Assert.ThrowsAsync<ReleaseException>(() =>
                Client(missing.Server).DownloadVerifiedAsync(missing.Info, "Clipvey.exe", Path.Combine(directory, "404"), publicKey));
            Assert.Equal(ReleaseFailure.Download, error.Failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // MARK: - Вспомогательное

    private static ReleaseClient Client(FakeServer server)
    {
        var http = new HttpClient(server);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Clipvey/0.1.0");
        return new ReleaseClient(http, allowLoopbackHttp: false);
    }

    private static void AssertVerificationFails(Action action) =>
        Assert.Equal(ReleaseFailure.Verification, Assert.Throws<ReleaseException>(action).Failure);

    private static string Sha256(string text) => ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string PublicKeyBase64(ECDsa key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return Convert.ToBase64String([0x04, .. q.X!, .. q.Y!]);
    }

    /// Релиз на подставном сервере: SHA256SUMS считается по signedExe, отдаётся servedExe.
    private static (FakeServer Server, ReleaseInfo Info) Release(ECDsa key, byte[] signedExe, byte[] servedExe)
    {
        var sums = Encoding.UTF8.GetBytes($"{new string('0', 64)}  Clipvey-mac.zip\n{ToHex(SHA256.HashData(signedExe))}  Clipvey.exe\n");
        var signature = Convert.ToBase64String(key.SignData(sums, HashAlgorithmName.SHA256));
        Assert.Equal(88, signature.Length);
        var server = new FakeServer
        {
            ["https://dl.test/SHA256SUMS"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sums) },
            ["https://dl.test/SHA256SUMS.sig"] = FakeServer.Ok(signature),
            ["https://dl.test/Clipvey.exe"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(servedExe) },
        };
        var info = new ReleaseInfo("v0.2.0", new ReleaseVersion(0, 2, 0), new Dictionary<string, Uri>
        {
            ["Clipvey.exe"] = new("https://dl.test/Clipvey.exe"),
            ["SHA256SUMS"] = new("https://dl.test/SHA256SUMS"),
            ["SHA256SUMS.sig"] = new("https://dl.test/SHA256SUMS.sig"),
        });
        return (server, info);
    }

    /// Подставной сервер: адрес → ответ. Запоминает запросы.
    private sealed class FakeServer : HttpMessageHandler, IEnumerable<KeyValuePair<string, Func<HttpResponseMessage>>>
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpResponseMessage> this[string url]
        {
            set => _routes[url] = value;
        }

        public static Func<HttpResponseMessage> Ok(string body, string? finalUrl = null) => () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            if (finalUrl is not null)
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl);
            return response;
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!_routes.TryGetValue(request.RequestUri!.ToString(), out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var response = route();
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }

        public IEnumerator<KeyValuePair<string, Func<HttpResponseMessage>>> GetEnumerator() => _routes.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
