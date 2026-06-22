using System.Diagnostics;

namespace RunnerRunner.Server.Services;

public sealed class GitHubApiUsageHandler : DelegatingHandler
{
    private readonly GitHubApiUsageTracker tracker;

    public GitHubApiUsageHandler(GitHubApiUsageTracker tracker)
    {
        this.tracker = tracker;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        finally
        {
            sw.Stop();
            tracker.Record(request, response, sw.Elapsed);
        }
    }
}
