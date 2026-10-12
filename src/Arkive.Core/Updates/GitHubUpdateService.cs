using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arkive.Core.Updates;

public sealed class GitHubUpdateService(HttpClient client)
{
    public async Task<ArkiveRelease?> CheckAsync(Version currentVersion, string? accessToken, CancellationToken cancellationToken = default)
    {
        using var request = ApiRequest($"releases/latest", accessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("No accessible release was found. For this private repository, add your own read-only GitHub token in Settings and ensure your account has repository access.");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var release = document.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
                !ArkiveReleasePolicy.TryParseVersion(release.GetProperty("tag_name").GetString(), out var version) ||
                version <= currentVersion) return null;
            var name = ArkiveReleasePolicy.InstallerName(version);
            var assets = release.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("The release must contain exactly one matching Windows installer.");
            var asset = assets[0];
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            var hash = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null;
            var id = asset.GetProperty("id").GetInt64();
            if (id <= 0 || size <= 0 || size > ArkiveReleasePolicy.MaximumInstallerSize ||
                hash is null || !ArkiveReleasePolicy.Sha256Pattern().IsMatch(hash))
                throw new InvalidDataException("The release installer has missing or invalid size/SHA-256 evidence.");
            return new ArkiveRelease(version, id, name, size, hash.ToUpperInvariant());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("The release metadata is malformed; no update was approved.", exception); }
    }

    public async Task<string> DownloadAsync(ArkiveRelease release, string directory, string? accessToken,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.FileName != ArkiveReleasePolicy.InstallerName(release.Version) || release.AssetId <= 0 ||
            release.Size <= 0 || release.Size > ArkiveReleasePolicy.MaximumInstallerSize ||
            !ArkiveReleasePolicy.Sha256Pattern().IsMatch(release.Sha256))
            throw new InvalidDataException("Invalid installer metadata.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.FileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.download";
        try
        {
            using var request = ApiRequest($"releases/assets/{release.AssetId}", accessToken);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var initial = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            HttpResponseMessage? redirected = null;
            try
            {
                var response = initial;
                if (initial.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
                {
                    var location = initial.Headers.Location;
                    if (location is null || !location.IsAbsoluteUri || !ArkiveReleasePolicy.IsAllowedDownload(location))
                        throw new InvalidDataException("The installer download redirected to an untrusted host.");
                    using var download = new HttpRequestMessage(HttpMethod.Get, location);
                    redirected = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response = redirected;
                }
                response.EnsureSuccessStatusCode();
                if (response.Headers.Location is not null ||
                    response.Content.Headers.ContentLength is { } length && length != release.Size)
                    throw new InvalidDataException("Unexpected installer download response.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += read;
                        if (total > release.Size) throw new InvalidDataException("The installer exceeds its approved size.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report((double)total / release.Size);
                    }
                    if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Installer SHA-256 or size verification failed. Nothing was executed.");
                }
            }
            finally { redirected?.Dispose(); }
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static HttpRequestMessage ApiRequest(string route, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ArkiveReleasePolicy.Repository}/{route}");
        request.Headers.UserAgent.ParseAdd("Arkive/0.1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Release metadata is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Release metadata is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
