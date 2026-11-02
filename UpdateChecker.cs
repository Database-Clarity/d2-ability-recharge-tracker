using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace D2AbilityTracker;

internal sealed record UpdateRelease(
    Version Version,
    string Tag,
    string AssetName,
    Uri DownloadUrl,
    long Size,
    string? Sha256,
    Uri PageUrl);

internal sealed record ChangelogRelease(Version Version, string Tag, string Body, Uri PageUrl);

internal static class UpdateChecker
{
    public const string Repository = "Database-Clarity/d2-ability-recharge-tracker";
    public const string LatestReleaseEndpoint = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const long MaximumInstallerBytes = 200L * 1024 * 1024;
    private const string ReleasesEndpoint = $"https://api.github.com/repos/{Repository}/releases";
    private const string GitHubApiHost = "api.github.com";
    private static readonly Uri LatestReleaseUri = new(LatestReleaseEndpoint);
    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } = ResolveCurrentVersion();

    // Local builds keep the 0.1.0 project version; CI release builds are always stamped with a later patch version.
    public static bool IsReleaseBuild => CurrentVersion > new Version(0, 1, 0);

    public static Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken) =>
        CheckAsync(Http, CurrentVersion, cancellationToken);

    internal static async Task<UpdateRelease?> CheckAsync(
        HttpClient http,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureGitHubSuccess(response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseLatestRelease(json, currentVersion);
    }

    public static async Task<IReadOnlyList<ChangelogRelease>> GetChangelogAsync(
        Version previousVersion,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        var releases = await FetchStableReleasesAsync(cancellationToken);
        return releases
            .Where(release => Normalize(release.Version) > Normalize(previousVersion) &&
                Normalize(release.Version) <= Normalize(currentVersion))
            .OrderByDescending(release => Normalize(release.Version))
            .ToArray();
    }

    public static async Task<IReadOnlyList<ChangelogRelease>> GetRecentChangelogAsync(
        Version currentVersion,
        int maximumReleases,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReleases);
        var releases = await FetchStableReleasesAsync(cancellationToken);
        return releases
            .Where(release => Normalize(release.Version) <= Normalize(currentVersion))
            .OrderByDescending(release => Normalize(release.Version))
            .Take(maximumReleases)
            .ToArray();
    }

    internal static IReadOnlyList<ChangelogRelease> ParseChangelogReleases(
        string json,
        Version previousVersion,
        Version currentVersion)
    {
        return ParseReleasePage(json)
            .Where(release => Normalize(release.Version) > Normalize(previousVersion) &&
                Normalize(release.Version) <= Normalize(currentVersion))
            .OrderByDescending(release => Normalize(release.Version))
            .ToArray();
    }

    internal static Uri? ParseNextPageUri(string? linkHeader)
    {
        if (string.IsNullOrWhiteSpace(linkHeader))
        {
            return null;
        }

        foreach (var link in linkHeader.Split(','))
        {
            var match = Regex.Match(link, @"^\s*<(?<url>[^>]+)>\s*;\s*(?<parameters>.*)$");
            if (!match.Success)
            {
                continue;
            }

            var relation = Regex.Match(match.Groups["parameters"].Value, @"(?:^|;)\s*rel\s*=\s*""?(?<rel>[^"";\s]+)");
            if (relation.Success &&
                string.Equals(relation.Groups["rel"].Value, "next", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var next))
            {
                return next;
            }
        }

        return null;
    }

    public static UpdateRelease? ParseLatestRelease(string json, Version currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True ||
            !root.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.GetString() is not { } tag ||
            !TryParseTag(tag, out var version) ||
            Normalize(version) <= Normalize(currentVersion))
        {
            return null;
        }

        var expectedName = $"D2AbilityTracker-{tag}-Setup.exe";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), expectedName, StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var url) &&
                Uri.TryCreate(url.GetString(), UriKind.Absolute, out var downloadUrl) &&
                downloadUrl.Scheme == Uri.UriSchemeHttps)
            {
                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
                string? sha256 = null;
                if (asset.TryGetProperty("digest", out var digest) &&
                    digest.GetString() is { } digestText &&
                    digestText.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = digestText["sha256:".Length..].ToLowerInvariant();
                }

                var pageUrl = root.TryGetProperty("html_url", out var html) && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var page)
                    ? page
                    : new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
                return new UpdateRelease(version, tag, expectedName, downloadUrl, size, sha256, pageUrl);
            }
        }

        return null;
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (!Regex.IsMatch(text, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant))
        {
            version = null!;
            return false;
        }

        return Version.TryParse(text, out version!) && version.Build >= 0;
    }

    public static async Task<string> DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (release.Size > MaximumInstallerBytes)
        {
            throw new InvalidDataException("The update installer is unexpectedly large.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "D2AbilityTracker-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.AssetName);
        var partialPath = path + ".download";
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += read;
                if (received > MaximumInstallerBytes)
                {
                    throw new InvalidDataException("The update installer is unexpectedly large.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (total > 0)
                {
                    progress?.Report(Math.Clamp(received / (double)total, 0, 1));
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (release.Sha256 is { } expected && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded installer failed its SHA-256 integrity check.");
            }
        }

        File.Move(partialPath, path, overwrite: true);
        return path;
    }

    public static void LaunchInstaller(string installerPath)
    {
        // /RELAUNCH=1 tells the installer to restart the tracker once the silent update finishes.
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1")
        {
            UseShellExecute = true
        });
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static async Task<IReadOnlyList<ChangelogRelease>> FetchStableReleasesAsync(CancellationToken cancellationToken)
    {
        var releases = new List<ChangelogRelease>();
        var nextPage = new Uri($"{ReleasesEndpoint}?per_page=100&page=1");
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visitedPages.Add(nextPage.AbsoluteUri))
            {
                throw new InvalidDataException("GitHub returned a repeated release page while loading the changelog.");
            }

            using var response = await Http.GetAsync(nextPage, cancellationToken);
            EnsureGitHubSuccess(response);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            releases.AddRange(ParseReleasePage(json));

            var links = response.Headers.TryGetValues("Link", out var values) ? string.Join(",", values) : null;
            var followingPage = ParseNextPageUri(links);
            if (followingPage is null)
            {
                break;
            }

            if (followingPage.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(followingPage.Host, GitHubApiHost, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(followingPage.AbsolutePath, new Uri(ReleasesEndpoint).AbsolutePath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("GitHub returned an unexpected release pagination URL.");
            }

            nextPage = followingPage;
        }

        return releases
            .GroupBy(release => release.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<ChangelogRelease> ParseReleasePage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub returned an invalid release list.");
        }

        var releases = new List<ChangelogRelease>();
        foreach (var root in document.RootElement.EnumerateArray())
        {
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String ||
                tagElement.GetString() is not { } tag ||
                !TryParseTag(tag, out var version))
            {
                continue;
            }

            var body = root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString() ?? string.Empty
                : string.Empty;
            var pageUrl = root.TryGetProperty("html_url", out var html) &&
                html.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(html.GetString(), UriKind.Absolute, out var page) &&
                page.Scheme == Uri.UriSchemeHttps &&
                string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                    ? page
                    : new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            releases.Add(new ChangelogRelease(version, tag, body, pageUrl));
        }

        return releases;
    }

    private static void EnsureGitHubSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "GitHub's update check limit was reached; try again later."
                : $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static Version ResolveCurrentVersion()
    {
        var informational = typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            var plus = informational.IndexOf('+');
            if (TryParseTag(plus >= 0 ? informational[..plus] : informational, out var parsed))
            {
                return parsed;
            }
        }

        return typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("D2AbilityTracker", ResolveCurrentVersion().ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
