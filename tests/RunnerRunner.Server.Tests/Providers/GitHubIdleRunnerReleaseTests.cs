using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Providers;

/// <summary>
/// <see cref="GitHubActionsProvider.ReleaseIdleRunnerAsync"/> is what every stop of a runner
/// that hasn't provably finished a job of its own now waits on: it must never delete, or
/// report as releasable, a runner GitHub says is running a job.
/// </summary>
public class GitHubIdleRunnerReleaseTests
{
    private const string Repo = "octo/fleet";
    private const string RunnersPath = "/repos/octo/fleet/actions/runners";

    [Fact]
    public async Task BusyRunner_IsNeverDeleted()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsList(RunnersPath), RunnersJson(("linux-jit-a", 11, true)));
        var provider = CreateProvider(api);

        var outcome = await provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a");

        Assert.Equal(RunnerReleaseOutcome.Busy, outcome);
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task IdleRunner_IsDeregistered()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsList(RunnersPath), RunnersJson(("linux-jit-a", 11, false), ("linux-jit-b", 12, true)))
            .Respond(r => r.Method == HttpMethod.Delete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var provider = CreateProvider(api);

        var outcome = await provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a");

        Assert.Equal(RunnerReleaseOutcome.Deregistered, outcome);
        var delete = Assert.Single(api.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal($"{RunnersPath}/11", delete.PathAndQuery);
    }

    [Fact]
    public async Task RunnerThatTakesAJobBeforeTheDelete_IsReportedBusy()
    {
        // GitHub refuses to delete a runner that is running a job.
        var api = new FakeProviderHttpApi()
            .RespondJson(IsList(RunnersPath), RunnersJson(("linux-jit-a", 11, false)))
            .RespondJson(
                r => r.Method == HttpMethod.Delete,
                """{"message":"Bad request - Runner \"linux-jit-a\" is still running a job\""}""",
                HttpStatusCode.UnprocessableEntity);
        var provider = CreateProvider(api);

        var outcome = await provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a");

        Assert.Equal(RunnerReleaseOutcome.Busy, outcome);
    }

    [Fact]
    public async Task RunnerGitHubNoLongerHas_IsNotRegistered()
    {
        // The repository has no such runner, and the owner's organization endpoint doesn't
        // exist (a user account): the runner already ran its one job and left.
        var api = new FakeProviderHttpApi()
            .RespondJson(IsList(RunnersPath), RunnersJson(("linux-jit-b", 12, true)))
            .RespondJson(IsList("/orgs/octo/actions/runners"), """{"message":"Not Found"}""", HttpStatusCode.NotFound);
        var provider = CreateProvider(api);

        var outcome = await provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a");

        Assert.Equal(RunnerReleaseOutcome.NotRegistered, outcome);
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RunnerBeyondTheFirstPage_IsFound()
    {
        var firstPage = Enumerable.Range(1, 100).Select(i => ($"static-{i}", (long)i, false)).ToArray();
        var api = new FakeProviderHttpApi()
            .RespondJson(
                r => IsList(RunnersPath)(r) && r.RequestUri!.Query.Contains("&page=1", StringComparison.Ordinal),
                RunnersJson(101, firstPage))
            .RespondJson(
                r => IsList(RunnersPath)(r) && r.RequestUri!.Query.Contains("&page=2", StringComparison.Ordinal),
                RunnersJson(101, ("linux-jit-a", 4242, true)));
        var provider = CreateProvider(api);

        var outcome = await provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a");

        Assert.Equal(RunnerReleaseOutcome.Busy, outcome);
    }

    [Fact]
    public async Task LooksWhereTheRunnerWasRegistered_NotAtTheCredentialsDefaultTarget()
    {
        // JIT runners are registered at the webhook job's repository. A credential whose
        // default installation is another owner must not send the check (or the delete) there.
        var credential = Credential();
        credential.GitHubAppInstallations =
        [
            new GitHubAppInstallation { Owner = "elsewhere", InstallationId = "1", IsDefault = true }
        ];
        var api = new FakeProviderHttpApi()
            .RespondJson(IsList(RunnersPath), RunnersJson(("linux-jit-a", 11, true)));
        var provider = CreateProvider(api);

        var outcome = await provider.ReleaseIdleRunnerAsync(credential, Repo, "linux-jit-a");

        Assert.Equal(RunnerReleaseOutcome.Busy, outcome);
        Assert.All(api.Requests, r => Assert.StartsWith(RunnersPath, r.PathAndQuery, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GitHubThatCantBeAsked_Throws(HttpStatusCode status)
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsList(RunnersPath), """{"message":"nope"}""", status);
        var provider = CreateProvider(api);

        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a"));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task NoScopeThatCanBeListed_Throws()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(_ => true, """{"message":"Not Found"}""", HttpStatusCode.NotFound);
        var provider = CreateProvider(api);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => provider.ReleaseIdleRunnerAsync(Credential(), Repo, "linux-jit-a"));
    }

    private static GitHubActionsProvider CreateProvider(FakeProviderHttpApi api) =>
        new(api, new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance),
            NullLogger<GitHubActionsProvider>.Instance);

    private static ProviderCredential Credential() => new()
    {
        Id = "cred",
        Name = "github",
        Provider = RunnerProvider.GitHubActions,
        GitHubToken = "fake-token",
        GitHubOrg = "octo",
        GitHubRepo = Repo
    };

    private static Func<HttpRequestMessage, bool> IsList(string path) =>
        r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == path;

    private static string RunnersJson(params (string Name, long Id, bool Busy)[] runners) =>
        RunnersJson(runners.Length, runners);

    private static string RunnersJson(int total, params (string Name, long Id, bool Busy)[] runners) =>
        JsonSerializer.Serialize(new
        {
            total_count = total,
            runners = runners.Select(r => new { id = r.Id, name = r.Name, status = "online", busy = r.Busy })
        });
}
