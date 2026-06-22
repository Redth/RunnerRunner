using System.Net;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class GitHubApiUsageTrackerTests
{
    [Fact]
    public void Record_GroupsGitHubRestRequestsByNormalizedEndpointAndScope()
    {
        var tracker = new GitHubApiUsageTracker();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/actions/runner/actions/runs/123/jobs?per_page=100");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "5000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "4998");
        response.Headers.TryAddWithoutValidation("x-ratelimit-used", "2");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1893456000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-resource", "core");

        tracker.Record(request, response, TimeSpan.FromMilliseconds(42));

        var item = Assert.Single(tracker.GetSnapshot().Summaries);
        Assert.Equal("GET", item.Method);
        Assert.Equal(GitHubApiRequestKind.Rest, item.Kind);
        Assert.Equal("repo:actions/runner", item.Scope);
        Assert.Equal("/repos/{owner}/{repo}/actions/runs/{id}/jobs?per_page={value}", item.Endpoint);
        Assert.Equal("Workflow/job reconciliation", item.Category);
        Assert.Equal(1, item.RequestCount);
        Assert.Equal(4998, item.LastRateLimit.Remaining);
        Assert.Equal("core", item.LastRateLimit.Resource);
    }

    [Fact]
    public void Record_GroupsGraphQLRequests()
    {
        var tracker = new GitHubApiUsageTracker();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        tracker.Record(request, response, TimeSpan.Zero);

        var item = Assert.Single(tracker.GetSnapshot().Summaries);
        Assert.Equal(GitHubApiRequestKind.GraphQL, item.Kind);
        Assert.Equal("/graphql", item.Endpoint);
        Assert.Equal("GraphQL", item.Category);
    }

    [Fact]
    public void Record_IgnoresGitHubDownloadUrls()
    {
        var tracker = new GitHubApiUsageTracker();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://github.com/actions/runner/releases/download/v2.328.0/actions-runner-linux-x64-2.328.0.tar.gz");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        tracker.Record(request, response, TimeSpan.Zero);

        Assert.Empty(tracker.GetSnapshot().Summaries);
    }

    [Fact]
    public void Record_CountsRateLimitedResponses()
    {
        var tracker = new GitHubApiUsageTracker();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/actions/runner/releases/latest");
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");

        tracker.Record(request, response, TimeSpan.Zero);

        var item = Assert.Single(tracker.GetSnapshot().Summaries);
        Assert.Equal(1, item.FailureCount);
        Assert.Equal(1, item.RateLimitedCount);
    }
}
