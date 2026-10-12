using System.Net;
using Arkive.Core.Updates;

namespace Arkive.SmokeTests;

internal static class UpdateSmokeTests
{
    internal static IReadOnlyList<SmokeTestCase> Cases { get; } =
    [
        new("validates stable update metadata and blocks downgrades", TestMetadata),
        new("verifies update downloads and keeps tokens off redirected hosts", TestDownload),
        new("rejects untrusted redirects, wrong hashes and oversized update downloads", TestUnsafeDownloads),
        new("keeps installed Arkive data outside the application folder", TestDataRoot),
        new("explains every update failure with a reason and a next step", TestFailureExplanations),
    ];

    private static void TestMetadata()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert(request.RequestUri!.Host == "api.github.com", "release metadata left the API host");
            Assert(request.Headers.Authorization?.Parameter == "test-token", "token authentication was not supplied");
            return JsonResponse(ReleaseJson());
        }));
        var service = new GitHubUpdateService(client);
        var release = service.CheckAsync(new Version(0, 1, 0), "test-token").GetAwaiter().GetResult();
        Assert(release is { Version: { Major: 0, Minor: 2, Build: 0 }, AssetId: 123 }, "new stable release was not resolved");
        Assert(service.CheckAsync(new Version(0, 2, 0), "test-token").GetAwaiter().GetResult() is null, "current version was offered again");
        Assert(service.CheckAsync(new Version(1, 0, 0), "test-token").GetAwaiter().GetResult() is null, "a downgrade was offered");
        foreach (var json in new[] { ReleaseJson(digest: ""), ReleaseJson(size: ArkiveReleasePolicy.MaximumInstallerSize + 1),
                     ReleaseJson(name: "other.exe"), ReleaseJson(id: 0) })
        {
            using var invalid = new HttpClient(new Handler(_ => JsonResponse(json)));
            Refuses(() => new GitHubUpdateService(invalid).CheckAsync(new Version(0, 1, 0), null).GetAwaiter().GetResult());
        }
        foreach (var tag in new[] { "v0.2.0-beta", "../0.2.0", "0.2", "1.0.0.0" })
            Assert(!ArkiveReleasePolicy.TryParseVersion(tag, out _), "non-stable release version accepted");
        foreach (var json in new[] { "{}", "[]", "not json", ReleaseJson().Replace("\"draft\":false", "\"draft\":\"no\"") })
        {
            using var invalid = new HttpClient(new Handler(_ => JsonResponse(json)));
            Refuses(() => new GitHubUpdateService(invalid).CheckAsync(new Version(0, 1, 0), null).GetAwaiter().GetResult());
        }
    }

    private static void TestDownload()
    {
        byte[] bytes = [1, 2, 3, 4];
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert(request.Headers.Authorization?.Parameter == "test-token", "asset API request was not authenticated");
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/installer");
                return response;
            }
            Assert(request.Headers.Authorization is null, "access token leaked to a download host");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var root = Path.Combine(fixtureDirectory, "verified-update");
        var release = new ArkiveRelease(new Version(0, 2, 0), 123, "Arkive-Setup-0.2.0-win-x64.exe", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        var path = new GitHubUpdateService(client).DownloadAsync(release, root, "test-token").GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes) && calls == 2, "verified installer was not downloaded");
        Assert(!Directory.EnumerateFiles(root, "*.download").Any(), "temporary installer was retained");
    }

    private static void TestUnsafeDownloads()
    {
        foreach (var host in new[] { "https://evil.example/file", "http://release-assets.githubusercontent.com/file", "file:///C:/file", "https://release-assets.githubusercontent.com:8443/file" })
        {
            using var client = new HttpClient(new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(host);
                return response;
            }));
            Refuses(() => new GitHubUpdateService(client).DownloadAsync(ValidRelease(), Path.Combine(fixtureDirectory, "unsafe-update"), "test-token").GetAwaiter().GetResult());
        }
        using (var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 8, 9, 0]) })))
            Refuses(() => new GitHubUpdateService(client).DownloadAsync(ValidRelease(), Path.Combine(fixtureDirectory, "bad-hash"), null).GetAwaiter().GetResult());
        using (var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4, 5]) })))
            Refuses(() => new GitHubUpdateService(client).DownloadAsync(ValidRelease(), Path.Combine(fixtureDirectory, "bad-size"), null).GetAwaiter().GetResult());
        Assert(!Directory.EnumerateFiles(fixtureDirectory, "*.download", SearchOption.AllDirectories).Any(), "failed downloads were not removed");
    }

    private static void TestDataRoot()
    {
        var app = Path.Combine(fixtureDirectory, "installed-app");
        var data = Path.Combine(fixtureDirectory, "local-data");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, ArkiveRuntimePaths.InstallationMarker), "{}");
        Assert(ArkiveRuntimePaths.ResolveDataRoot(null, app, "project", data, "current") == Path.Combine(data, "Arkive"), "installed data used the application folder");
        Assert(ArkiveRuntimePaths.ResolveDataRoot(fixtureDirectory, app, null, data, "current") == fixtureDirectory, "explicit existing data root was ignored");
        Assert(ArkiveRuntimePaths.ResolveDataRoot(null, fixtureDirectory, fixtureDirectory, data, "current") == fixtureDirectory, "source checkout compatibility changed");
    }

    private static void TestFailureExplanations()
    {
        var expectedAgent = $"Arkive/{ArkiveReleasePolicy.CurrentVersion.ToString(3)}";
        using (var client = new HttpClient(new Handler(request =>
               {
                   Assert(request.Headers.UserAgent.ToString() == expectedAgent, "update requests did not identify the running version");
                   return new HttpResponseMessage(HttpStatusCode.NotFound);
               })))
        {
            var missing = Fails(() => new GitHubUpdateService(client).CheckAsync(new Version(0, 1, 0), null).GetAwaiter().GetResult());
            Assert(missing is HttpRequestException { StatusCode: HttpStatusCode.NotFound } &&
                   Explain(ArkiveUpdateStage.Check, missing).Reason.Contains("no published Arkive release"), "a missing release was not explained");
        }
        using (var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
        {
            var limited = Explain(ArkiveUpdateStage.Check, Fails(() => new GitHubUpdateService(client).CheckAsync(new Version(0, 1, 0), null).GetAwaiter().GetResult()));
            Assert(limited.Reason.Contains("HTTP 403") && limited.Advice.Contains("Wait up to an hour"), "a GitHub refusal was not explained");
        }
        using (var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 8, 9, 0]) })))
        {
            var tampered = Explain(ArkiveUpdateStage.Download, Fails(() => new GitHubUpdateService(client).DownloadAsync(ValidRelease(), Path.Combine(fixtureDirectory, "explained-hash"), null).GetAwaiter().GetResult()));
            Assert(tampered.Reason.Contains("SHA-256") && tampered.Advice.Contains("nothing was run"), "a hash mismatch was not explained");
        }
        using (var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) })))
        {
            var truncated = Explain(ArkiveUpdateStage.Download, Fails(() => new GitHubUpdateService(client).DownloadAsync(ValidRelease(), Path.Combine(fixtureDirectory, "explained-size"), null).GetAwaiter().GetResult()));
            Assert(truncated.Reason.Contains("does not match the 4-byte installer"), "a truncated download was not explained");
        }
        Assert(Explain(ArkiveUpdateStage.Check, new HttpRequestException("No such host is known.")).Reason.Contains("could not connect to GitHub"), "an offline check was not explained");
        Assert(Explain(ArkiveUpdateStage.Download, new TaskCanceledException()).Reason.Contains("did not respond in time"), "a download timeout was not explained");
        Assert(ArkiveUpdateFailure.Describe(ArkiveUpdateStage.Download, new OperationCanceledException(), cancelledByUser: true).Reason == "You cancelled the download.", "a user cancellation was reported as a failure");
        Assert(Explain(ArkiveUpdateStage.Install, new TaskCanceledException()).Reason.Contains("did not finish in time"), "an install timeout blamed GitHub");
        Assert(Explain(ArkiveUpdateStage.Download, new UnauthorizedAccessException("Access to the path is denied.")).Reason.Contains("denied access"), "a permission failure was not explained");
        foreach (var (code, reason) in new[] { (1, "Another Arkive window"), (5, "could not be replaced"), (8, "must restart"), (99, "unexpected exit code 99") })
        {
            var exit = Explain(ArkiveUpdateStage.Install, ArkiveUpdateFailure.InstallerExit(code, @"C:\updates\Arkive-Setup-9.9.9-win-x64.exe", @"C:\updates\install.log"));
            Assert(exit.Reason.Contains(reason) && exit.Advice.Contains(@"C:\updates\install.log") && exit.Advice.Contains("Arkive-Setup-9.9.9"),
                $"installer exit code {code} was not explained");
        }
    }

    private static ArkiveUpdateFailure Explain(ArkiveUpdateStage stage, Exception exception)
    {
        var failure = ArkiveUpdateFailure.Describe(stage, exception);
        Assert(failure.Text.Contains("Why: " + failure.Reason) && failure.Text.Contains("What to do: " + failure.Advice) &&
               failure.Advice.Length > 0 && !failure.Text.Contains("private", StringComparison.OrdinalIgnoreCase), "an update failure lacked a reason or next step");
        return failure;
    }

    private static Exception Fails(Action action)
    {
        try { action(); }
        catch (Exception exception) { return exception; }
        throw new InvalidOperationException("The update operation unexpectedly succeeded.");
    }

    private static ArkiveRelease ValidRelease() => new(new Version(0, 2, 0), 123, "Arkive-Setup-0.2.0-win-x64.exe", 4, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })));
    private static string ReleaseJson(string? digest = null, long size = 4, string name = "Arkive-Setup-0.2.0-win-x64.exe", long id = 123) =>
        JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v0.2.0", assets = new[] { new { id, name, size, digest = digest ?? "sha256:" + ValidRelease().Sha256 } } });
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static void Refuses(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe update was not rejected.");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
