using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Grains;

[Collection(OrleansClusterCollection.Name)]
public sealed class WebhookProcessorGrainTests
{
    private static long _jobIdSeed = 100_000;

    private readonly IGrainFactory _grainFactory;
    private readonly IDocumentStore _store;

    public WebhookProcessorGrainTests(OrleansTestClusterFixture fixture)
    {
        _grainFactory = fixture.GrainFactory;
        _store = fixture.Cluster.GetSiloServiceProvider(null!).GetRequiredService<IDocumentStore>();
    }

    [Fact]
    public void ValidateHmac_AcceptsGitHubSha256Signature()
    {
        const string body = """{"action":"queued"}""";
        const string secret = "github-secret";
        var signature = "sha256=" + ComputeSignature(body, secret);

        var result = WebhookProcessorGrain.ValidateHmac(body, secret, signature, "github");

        Assert.True(result);
    }

    [Fact]
    public void ValidateHmac_AcceptsGiteaSignatureWithoutPrefix()
    {
        const string body = """{"action":"queued"}""";
        const string secret = "gitea-secret";
        var signature = ComputeSignature(body, secret);

        var result = WebhookProcessorGrain.ValidateHmac(body, secret, signature, "gitea");

        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=bad")]
    public void ValidateHmac_RejectsMissingOrWrongSignature(string? signature)
    {
        var result = WebhookProcessorGrain.ValidateHmac("""{"action":"queued"}""", "secret", signature, "github");

        Assert.False(result);
    }

    [Fact]
    public void ResolveWebhookSecret_PrefersRuleSecretOverCredentialSecret()
    {
        var rule = new ProvisioningRule
        {
            Name = "webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            WebhookSecret = "rule-secret"
        };
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "123",
            GitHubAppPrivateKey = "private-key",
            GitHubAppWebhookSecret = "credential-secret"
        };

        var secret = WebhookProcessorGrain.ResolveWebhookSecret(rule, credential, RunnerProvider.GitHubActions);

        Assert.Equal("rule-secret", secret);
    }

    [Fact]
    public void ResolveWebhookSecret_FallsBackToGitHubAppWebhookSecret()
    {
        var rule = new ProvisioningRule
        {
            Name = "webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions
        };
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "123",
            GitHubAppPrivateKey = "private-key",
            GitHubAppWebhookSecret = "credential-secret"
        };

        var secret = WebhookProcessorGrain.ResolveWebhookSecret(rule, credential, RunnerProvider.GitHubActions);

        Assert.Equal("credential-secret", secret);
    }

    [Fact]
    public void ResolveWebhookSecret_DoesNotUseGitHubAppSecretForGitea()
    {
        var rule = new ProvisioningRule
        {
            Name = "webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GiteaActions
        };
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "123",
            GitHubAppPrivateKey = "private-key",
            GitHubAppWebhookSecret = "credential-secret"
        };

        var secret = WebhookProcessorGrain.ResolveWebhookSecret(rule, credential, RunnerProvider.GiteaActions);

        Assert.Null(secret);
    }

    [Fact]
    public async Task ProcessWebhook_GitHubQueued_UsesRepoScopedRuleAndStripsMagicLabels()
    {
        var id = OrleansTestIds.Create("github-webhook");
        var secret = $"{id}-secret";
        var profileId = $"{id}-profile";
        var repoRule = new ProvisioningRule
        {
            Id = $"{id}-repo-rule",
            Name = "repo rule",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            WebhookSecret = secret,
            AllowedRepos = ["octo-org/octo-repo"],
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    RequiredLabels = ["self-hosted", "linux"],
                    ProfileId = profileId
                }
            ]
        };
        var orgRule = new ProvisioningRule
        {
            Id = $"{id}-org-rule",
            Name = "org rule",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            WebhookSecret = secret,
            AllowedOrgs = ["octo-org"],
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    RequiredLabels = ["self-hosted", "linux"],
                    ProfileId = $"{id}-wrong-profile",
                    Priority = 100
                }
            ]
        };

        await _store.Insert(orgRule);
        await _store.Insert(repoRule);
        await _grainFactory.GetGrain<IProfileGrain>(profileId).SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "linux repo profile",
            Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Docker,
            AllowWebhookImageTagOverride = true
        });

        var jobId = NextJobId();
        var body = BuildWorkflowJobPayload(
            action: "queued",
            jobId: jobId,
            runId: jobId + 1,
            repository: "octo-org/octo-repo",
            labels: ["self-hosted", "linux", "rr-image-tag=2024.10", "extra"],
            installationId: "98765");

        var result = await Processor().ProcessWebhook("github", body, SignGitHub(body, secret));

        Assert.True(result.Success);
        Assert.Equal("provisioned", result.Status);
        Assert.Equal(profileId, result.ProfileId);
        Assert.False(string.IsNullOrWhiteSpace(result.EventId));

        var webhookEvent = await _store.Get<WebhookEvent>(result.EventId!);
        Assert.NotNull(webhookEvent);
        Assert.Equal(repoRule.Id, webhookEvent.BindingId);
        Assert.Equal(nameof(RunnerProvider.GitHubActions), webhookEvent.Provider);
        Assert.Equal("98765", webhookEvent.GitHubInstallationId);
        Assert.Equal(["self-hosted", "linux", "extra"], webhookEvent.Labels);
        Assert.Equal("2024.10", webhookEvent.ImageTagOverride);
        Assert.Null(webhookEvent.ImageTagOverrideRejectedReason);
    }

    [Fact]
    public async Task ProcessWebhook_GiteaQueued_RejectsRepositoryOutsideMatchedSecretScope()
    {
        var id = OrleansTestIds.Create("gitea-webhook");
        var secret = $"{id}-secret";
        var jobId = NextJobId();
        await _store.Insert(new ProvisioningRule
        {
            Id = $"{id}-rule",
            Name = "gitea scoped rule",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GiteaActions,
            WebhookSecret = secret,
            AllowedRepos = ["team/allowed"],
            LabelMappings =
            [
                new LabelProfileMapping
                {
                    RequiredLabels = ["*"],
                    ProfileId = $"{id}-profile"
                }
            ]
        });
        var body = BuildWorkflowJobPayload(
            action: "queued",
            jobId: jobId,
            runId: jobId + 1,
            repository: "team/other",
            labels: ["self-hosted", "linux"]);

        var result = await Processor().ProcessWebhook("gitea", body, ComputeSignature(body, secret));

        Assert.False(result.Success);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("Repository not in scope (checked 1 HMAC-matched rules)", result.Message);

        var webhookEvent = Assert.Single(await EventsForJob(jobId));
        Assert.Equal(nameof(RunnerProvider.GiteaActions), webhookEvent.Provider);
        Assert.Equal("rejected", webhookEvent.Status);
        Assert.Equal("Repository not in scope", webhookEvent.Error);
    }

    [Fact]
    public async Task ProcessWebhook_GitHubCompleted_PersistsCompletedEventForMatchedRule()
    {
        var id = OrleansTestIds.Create("github-completed");
        var secret = $"{id}-secret";
        var jobId = NextJobId();
        var rule = new ProvisioningRule
        {
            Id = $"{id}-rule",
            Name = "completed rule",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            WebhookSecret = secret,
            AllowedOrgs = ["octo-org"]
        };
        await _store.Insert(rule);
        var body = BuildWorkflowJobPayload(
            action: "completed",
            jobId: jobId,
            runId: jobId + 1,
            repository: "octo-org/another-repo",
            labels: ["self-hosted", "linux"]);

        var result = await Processor().ProcessWebhook("github", body, SignGitHub(body, secret));

        Assert.True(result.Success);
        Assert.Equal("completed", result.Status);

        var webhookEvent = Assert.Single(await EventsForJob(jobId));
        Assert.Equal(rule.Id, webhookEvent.BindingId);
        Assert.Equal("completed", webhookEvent.Action);
        Assert.Equal("completed", webhookEvent.Status);
        Assert.Equal("octo-org/another-repo", webhookEvent.Repository);
    }

    [Fact]
    public async Task ProcessWebhook_GitHubInProgress_MarksDynamicRunnerRunningAndPersistsEvent()
    {
        var id = OrleansTestIds.Create("github-progress");
        var secret = $"{id}-secret";
        var jobId = NextJobId();
        var profileId = $"{id}-profile";
        var hostId = $"{id}-host";
        var instanceId = $"{id}-runner";
        var rule = new ProvisioningRule
        {
            Id = $"{id}-rule",
            Name = "in-progress rule",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            WebhookSecret = secret,
            AllowedRepos = ["octo-org/octo-repo"]
        };
        await _store.Insert(rule);

        await _grainFactory.GetGrain<IHostGrain>(hostId)
            .Register("linux-host", HostPlatform.Linux, "x64", "1.0.0");
        await _grainFactory.GetGrain<IProfileGrain>(profileId).SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "linux profile",
            Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Docker
        });
        var runner = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        await runner.Initialize(
            hostId,
            profileId,
            "rr-jit-runner",
            "dynamic",
            jobId: jobId.ToString(),
            webhookEventId: $"{id}-queued-event",
            provisioningRuleId: rule.Id);

        var body = BuildWorkflowJobPayload(
            action: "in_progress",
            jobId: jobId,
            runId: jobId + 1,
            repository: "octo-org/octo-repo",
            labels: ["self-hosted", "linux"]);

        var result = await Processor().ProcessWebhook("github", body, SignGitHub(body, secret));

        Assert.True(result.Success);
        Assert.Equal("in_progress", result.Status);
        Assert.Equal(instanceId, result.InstanceId);

        var runnerState = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Running, runnerState.Status);
        Assert.Equal("Job in progress", runnerState.StatusMessage);

        var webhookEvent = Assert.Single(await EventsForJob(jobId), e => e.Action == "in_progress");
        Assert.Equal(rule.Id, webhookEvent.BindingId);
        Assert.Equal(instanceId, webhookEvent.InstanceId);
        Assert.Equal(profileId, webhookEvent.MatchedProfileId);
        Assert.Equal("in_progress", webhookEvent.Status);
    }

    private static string ComputeSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private IWebhookProcessorGrain Processor() =>
        _grainFactory.GetGrain<IWebhookProcessorGrain>(Random.Shared.NextInt64());

    private async Task<List<WebhookEvent>> EventsForJob(long jobId)
    {
        var jobIdString = jobId.ToString();
        return (await _store.Query<WebhookEvent>()
            .Where(e => e.JobId == jobIdString)
            .ToList()).ToList();
    }

    private static long NextJobId() => Interlocked.Increment(ref _jobIdSeed);

    private static string SignGitHub(string body, string secret) =>
        "sha256=" + ComputeSignature(body, secret);

    private static string BuildWorkflowJobPayload(
        string action,
        long jobId,
        long runId,
        string repository,
        string[] labels,
        string workflowName = "CI",
        string? installationId = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["workflow_job"] = new Dictionary<string, object?>
            {
                ["id"] = jobId,
                ["run_id"] = runId,
                ["workflow_name"] = workflowName,
                ["labels"] = labels
            },
            ["repository"] = new Dictionary<string, object?>
            {
                ["full_name"] = repository
            }
        };

        if (!string.IsNullOrWhiteSpace(installationId))
        {
            payload["installation"] = new Dictionary<string, object?>
            {
                ["id"] = installationId
            };
        }

        return JsonSerializer.Serialize(payload);
    }
}
