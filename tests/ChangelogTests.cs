using D2AbilityTracker;

internal static class ChangelogTests
{
    public static void Run()
    {
        Assert(UpdateChecker.LatestReleaseEndpoint.EndsWith("/releases/latest", StringComparison.Ordinal),
            "Update checks should continue using GitHub's latest-release endpoint.");

        const string latestRelease = """
            {
              "tag_name": "v2.4.0",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/tag/v2.4.0",
              "assets": [{
                "name": "D2AbilityTracker-v2.4.0-Setup.exe",
                "browser_download_url": "https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/download/v2.4.0/setup.exe",
                "size": 1
              }]
            }
            """;
        var offeredUpdate = UpdateChecker.ParseLatestRelease(latestRelease, new Version(2, 1, 0));
        Assert(offeredUpdate?.Version == new Version(2, 4, 0),
            "An update check should offer the latest release directly instead of an intermediate release.");
        CheckFreshUpdateClicksAsync(latestRelease).GetAwaiter().GetResult();

        const string releases = """
            [
              {"tag_name":"v3.0.0","draft":false,"prerelease":false,"body":"Too new"},
              {"tag_name":"v2.4.0","draft":false,"prerelease":false,"body":"Latest included"},
              {"tag_name":"v2.3.0-rc.1","draft":false,"prerelease":true,"body":"Preview"},
              {"tag_name":"v2.2.0","draft":false,"prerelease":false,"body":"Middle"},
              {"tag_name":"v2.1.0","draft":false,"prerelease":false,"body":"Exclusive lower bound"},
              {"tag_name":"v2.0.0","draft":true,"prerelease":false,"body":"Draft"}
            ]
            """;
        var range = UpdateChecker.ParseChangelogReleases(releases, new Version(2, 1, 0), new Version(2, 4, 0));
        Assert(range.Count == 2, "Changelog filtering should exclude drafts, prereleases, and versions outside the range.");
        Assert(range[0].Version == new Version(2, 4, 0) && range[0].Body == "Latest included",
            "Changelog filtering should include the upper bound and order newer releases first.");
        Assert(range[1].Version == new Version(2, 2, 0), "Changelog filtering should exclude the lower bound.");

        var nextPage = UpdateChecker.ParseNextPageUri(
            "<https://api.github.com/repos/Database-Clarity/d2-ability-recharge-tracker/releases?per_page=100&page=2>; rel=\"next\", " +
            "<https://api.github.com/repos/Database-Clarity/d2-ability-recharge-tracker/releases?per_page=100&page=4>; rel=\"last\"");
        Assert(nextPage?.Query.EndsWith("page=2", StringComparison.Ordinal) == true,
            "Changelog pagination should follow GitHub's next-page link.");
        Assert(UpdateChecker.ParseNextPageUri(
            "<https://api.github.com/repos/Database-Clarity/d2-ability-recharge-tracker/releases?per_page=100&page=1>; rel=\"prev\"") is null,
            "Changelog pagination should stop when GitHub has no next-page link.");
    }

    private static async Task CheckFreshUpdateClicksAsync(string initialRelease)
    {
        using var handler = new LatestReleaseHandler(initialRelease);
        using var http = new HttpClient(handler);
        var offers = new List<UpdateRelease>();
        Task<UpdateRelease?> Check() => UpdateChecker.CheckAsync(http, new Version(2, 1, 0), CancellationToken.None);
        Task Offer(UpdateRelease release)
        {
            offers.Add(release);
            return Task.CompletedTask;
        }

        await MainForm.CheckAndOfferUpdateAsync(Check, Offer);
        handler.ReleaseJson = initialRelease.Replace("v2.4.0", "v2.5.0", StringComparison.Ordinal);
        await MainForm.CheckAndOfferUpdateAsync(Check, Offer);
        Assert(handler.RequestCount == 2 && offers.Count == 2 &&
            offers[0].Version == new Version(2, 4, 0) && offers[1].Version == new Version(2, 5, 0),
            "Every update click must fetch and offer the actual latest release, even after discovering an earlier update.");

        handler.Fail = true;
        try
        {
            await MainForm.CheckAndOfferUpdateAsync(Check, Offer);
            throw new InvalidOperationException("The failing fresh update check should surface an error.");
        }
        catch (HttpRequestException)
        {
            Assert(handler.RequestCount == 3 && offers.Count == 2,
                "A failed fresh check must not offer or install a previously discovered release.");
        }

        await MainForm.CheckAndOfferUpdateAsync(() => Task.FromResult<UpdateRelease?>(null), Offer);
        Assert(offers.Count == 2, "A fresh check with no update must not reuse a previously offered release.");
    }

    private sealed class LatestReleaseHandler(string releaseJson) : HttpMessageHandler
    {
        public string ReleaseJson { get; set; } = releaseJson;
        public bool Fail { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert(request.RequestUri?.AbsoluteUri == UpdateChecker.LatestReleaseEndpoint,
                "Every click must request releases/latest, not an intermediate or cached release URL.");
            Assert(request.Headers.CacheControl?.NoCache == true,
                "Latest-release checks should request revalidation rather than a stale HTTP cache entry.");
            return Task.FromResult(new HttpResponseMessage(
                Fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson)
            });
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
