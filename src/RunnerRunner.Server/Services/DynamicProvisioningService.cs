using System.Collections.Concurrent;
using System.Text.Json;
using Orleans;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Events;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Webhooks;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Subscribes to webhook-triggered job-queued events and orchestrates JIT runner provisioning:
/// host selection → JIT config generation → deploy runner to HostWorker.
/// Also recovers queued webhook events that were matched but could not be fulfilled immediately.
/// </summary>
public class DynamicProvisioningService : BackgroundService
{
    private enum QueueProcessingOutcome
    {
        Advanced,
        Blocked
    }

    private sealed record HostSelectionResult(
        Host? Host,
        string? Reason,
        bool CapacityBlocked);

    private sealed record GitHubWorkflowRunRef(
        string RunId,
        string WorkflowName,
        string JobsUrl);

    private readonly ILogger<DynamicProvisioningService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly JitConfigService _jitConfigService;
    private readonly RunnerRegistrationCleanupService _runnerRegistrationCleanupService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubAuthenticationService _gitHubAuth;
    private readonly IHostCommandDispatcher _hostCommands;
    private readonly IGrainFactory _grainFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _jobLocks = new();
    private readonly ConcurrentDictionary<string, string> _runsEtagCache = new();
    private readonly ConcurrentDictionary<string, string> _jobsEtagCache = new();
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly TimeSpan _retrySweepInterval;
    private readonly TimeSpan _pendingTimeout;
    private readonly TimeSpan _githubPollInterval;
    private DateTime _lastGitHubPollAt = DateTime.MinValue;

    public DynamicProvisioningService(
        ILogger<DynamicProvisioningService> logger,
        IConfiguration configuration,
        IServiceProvider services,
        JitConfigService jitConfigService,
        RunnerRegistrationCleanupService runnerRegistrationCleanupService,
        IHttpClientFactory httpClientFactory,
        GitHubAuthenticationService gitHubAuth,
        IHostCommandDispatcher hostCommands,
        IGrainFactory grainFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _services = services;
        _jitConfigService = jitConfigService;
        _runnerRegistrationCleanupService = runnerRegistrationCleanupService;
        _httpClientFactory = httpClientFactory;
        _gitHubAuth = gitHubAuth;
        _hostCommands = hostCommands;
        _grainFactory = grainFactory;
        _retrySweepInterval = TimeSpan.FromSeconds(Math.Max(5, _configuration.GetValue("DynamicProvisioning:PendingRetrySeconds", 15)));
        _pendingTimeout = TimeSpan.FromMinutes(Math.Max(1, _configuration.GetValue("DynamicProvisioning:PendingTimeoutMinutes", 10)));
        _githubPollInterval = TimeSpan.FromSeconds(Math.Max(30, _configuration.GetValue("DynamicProvisioning:GitHubPollSeconds", 180)));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        WebhookEndpoints.OnJobQueued += HandleJobQueued;
        WebhookEndpoints.OnJobCompleted += HandleJobCompleted;
        StreamSubscriptionService.OnRunnerStatusChanged += HandleRunnerStatusChanged;
        StreamSubscriptionService.OnHostStatusChanged += HandleHostStatusChanged;
        _logger.LogInformation(
            "DynamicProvisioningService started (retry sweep: {RetrySweep}s, timeout: {Timeout}m, GitHub poll: {GitHubPoll}s)",
            _retrySweepInterval.TotalSeconds,
            _pendingTimeout.TotalMinutes,
            _githubPollInterval.TotalSeconds);
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        WebhookEndpoints.OnJobQueued -= HandleJobQueued;
        WebhookEndpoints.OnJobCompleted -= HandleJobCompleted;
        StreamSubscriptionService.OnRunnerStatusChanged -= HandleRunnerStatusChanged;
        StreamSubscriptionService.OnHostStatusChanged -= HandleHostStatusChanged;
        _logger.LogInformation("DynamicProvisioningService stopped");
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunQueueSweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while reconciling pending webhook events");
                }

                await Task.Delay(_retrySweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileQueuedGitHubJobsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var rules = (await store.Query<ProvisioningRule>().ToList())
            .Where(r => r.Enabled && r.Type == ProvisioningType.Webhook && r.Provider == RunnerProvider.GitHubActions)
            .ToList();

        if (rules.Count == 0)
            return;

        var existingQueuedKeys = (await store.Query<WebhookEvent>().ToList())
            .Where(ShouldBlockQueuedGitHubBackfill)
            .Select(e => $"{e.Repository}|{e.JobId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ProviderCredentialId))
                continue;

            var credential = await store.Get<ProviderCredential>(rule.ProviderCredentialId);
            if (credential == null)
                continue;

            foreach (var repo in ResolveReposToPoll(rule, credential))
            {
                if (!GitHubAuthenticationService.HasGitHubApiCredentials(credential, repository: repo))
                    continue;

                await BackfillQueuedJobsForRepoAsync(store, rule, credential, repo, existingQueuedKeys, ct);
            }
        }
    }

    private async Task BackfillQueuedJobsForRepoAsync(
        IDocumentStore store,
        ProvisioningRule rule,
        ProviderCredential credential,
        string repo,
        HashSet<string> existingQueuedKeys,
        CancellationToken ct)
    {
        var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";
        using var client = _httpClientFactory.CreateClient();
        await _gitHubAuth.ConfigureClientAsync(client, credential, repository: repo, ct: ct);

        foreach (var run in await ListRelevantGitHubWorkflowRunsAsync(client, apiUrl, repo, ct))
        {
            if (ct.IsCancellationRequested)
                return;

            var jobsUrl = string.IsNullOrWhiteSpace(run.JobsUrl)
                ? $"{apiUrl}/repos/{repo}/actions/runs/{run.RunId}/jobs?per_page=100"
                : $"{run.JobsUrl}{(run.JobsUrl.Contains('?') ? "&" : "?")}per_page=100";

            using var jobsRequest = new HttpRequestMessage(HttpMethod.Get, jobsUrl);
            if (_jobsEtagCache.TryGetValue(jobsUrl, out var cachedJobsEtag) && !string.IsNullOrEmpty(cachedJobsEtag))
                jobsRequest.Headers.TryAddWithoutValidation("If-None-Match", cachedJobsEtag);

            var jobsResponse = await client.SendAsync(jobsRequest, ct);

            // 304 Not Modified: job statuses unchanged since last poll — no new queued work to reconcile.
            // Conditional requests do not consume the primary rate limit.
            if (jobsResponse.StatusCode == System.Net.HttpStatusCode.NotModified)
                continue;

            if (!jobsResponse.IsSuccessStatusCode)
                continue;

            if (jobsResponse.Headers.ETag?.Tag is { Length: > 0 } jobsEtag)
                _jobsEtagCache[jobsUrl] = jobsEtag;

            using var jobsDoc = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync(ct));
            if (!jobsDoc.RootElement.TryGetProperty("jobs", out var jobs))
                continue;

            foreach (var job in jobs.EnumerateArray())
            {
                var status = job.TryGetProperty("status", out var statusProp)
                    ? statusProp.GetString()
                    : null;

                var jobId = job.GetProperty("id").GetInt64().ToString();
                var jobConclusion = job.TryGetProperty("conclusion", out var conclusionProp)
                    ? conclusionProp.GetString()
                    : null;

                var existingEventsForJob = (await store.Query<WebhookEvent>().ToList())
                    .Where(e => e.Provider == RunnerProvider.GitHubActions.ToString()
                        && e.Repository.Equals(repo, StringComparison.OrdinalIgnoreCase)
                        && e.JobId == jobId)
                    .ToList();

                if (string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
                {
                    var now = DateTime.UtcNow;
                    foreach (var existingEvent in existingEventsForJob.Where(e => e.Action == "queued" && e.Status != "in_progress"))
                    {
                        existingEvent.MarkResolved("in_progress", now, existingEvent.InstanceId);
                        await store.Update(existingEvent);
                    }

                    continue;
                }

                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    var now = DateTime.UtcNow;
                    foreach (var existingEvent in existingEventsForJob.Where(e => e.Action == "queued" && e.Status != "completed"))
                    {
                        existingEvent.MarkResolved("completed", now, existingEvent.InstanceId);
                        await store.Update(existingEvent);
                    }

                    await CleanupDynamicRunnersForJobAsync(
                        store,
                        jobId,
                        $"Job completed ({jobConclusion ?? "unknown"})",
                        removeRecords: true);

                    continue;
                }

                if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = $"{repo}|{jobId}";
                if (!existingQueuedKeys.Add(key))
                    continue;

                var labels = job.TryGetProperty("labels", out var labelsProp)
                    ? labelsProp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];

                var evt = new WebhookEvent
                {
                    BindingId = rule.Id,
                    Provider = RunnerProvider.GitHubActions.ToString(),
                    Action = "queued",
                    JobId = jobId,
                    RunId = run.RunId,
                    Repository = repo,
                    GitHubInstallationId = GitHubAuthenticationService.ResolveGitHubAppInstallationId(credential, repository: repo),
                    WorkflowName = run.WorkflowName,
                    Labels = labels,
                    Status = "pending",
                    Error = "Queued via GitHub API reconciliation after webhook delivery was missed",
                    ReceivedAt = DateTime.UtcNow,
                    NextRetryAt = DateTime.UtcNow,
                    ExpiresAt = null
                };

                var (matchedRule, profile, runnerDefinition, reason) = await ResolveProvisioningMatchAsync(store, evt, null);
                if (profile != null)
                {
                    evt.MatchedProfileId = profile.Id;
                    evt.MatchedProfileName = profile.Name;
                    evt.MatchedRunnerDefinitionId = runnerDefinition?.Id;
                    evt.MatchedRunnerDefinitionName = runnerDefinition?.Name;
                }
                else
                {
                    evt.Status = matchedRule?.IsMissingRunnerTargetRequest(evt.Labels) == true
                        ? WebhookEvent.StatusIgnoredTarget
                        : "no_match";
                    evt.Error = reason;
                }

                await store.Insert(evt);

                _logger.LogInformation(
                    "Backfilled queued GitHub workflow_job for {Repo} job {JobId} with labels [{Labels}]",
                    repo,
                    jobId,
                    string.Join(", ", labels));

                if (!string.IsNullOrWhiteSpace(evt.MatchedProfileId))
                    await HandleJobQueuedAsync(evt, evt.MatchedProfileId, isRecoveryAttempt: false);
            }
        }
    }

    internal static IReadOnlyList<string> BuildGitHubRunQueries(string apiUrl, string repo)
    {
        var root = $"{apiUrl.TrimEnd('/')}/repos/{repo}/actions/runs";

        // Status-scoped queries only — fetching all recent runs (including completed) balloons
        // into per-run jobs calls that burn through GitHub's rate limit. Terminal-state
        // transitions arrive via webhooks; reconciliation only needs to catch missed
        // queued/waiting/requested events (and in_progress, in case queued was missed).
        return
        [
            $"{root}?status=queued&per_page=100",
            $"{root}?status=in_progress&per_page=100",
            $"{root}?status=requested&per_page=100",
            $"{root}?status=waiting&per_page=100"
        ];
    }

    internal static bool ShouldBlockQueuedGitHubBackfill(WebhookEvent evt) =>
        string.Equals(evt.Provider, RunnerProvider.GitHubActions.ToString(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(evt.Action, "queued", StringComparison.OrdinalIgnoreCase)
        && (!evt.IsTerminal || string.Equals(evt.Status, WebhookEvent.StatusIgnoredTarget, StringComparison.OrdinalIgnoreCase));

    private async Task<List<GitHubWorkflowRunRef>> ListRelevantGitHubWorkflowRunsAsync(
        HttpClient client,
        string apiUrl,
        string repo,
        CancellationToken ct)
    {
        var runs = new Dictionary<string, GitHubWorkflowRunRef>(StringComparer.OrdinalIgnoreCase);

        foreach (var url in BuildGitHubRunQueries(apiUrl, repo))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_runsEtagCache.TryGetValue(url, out var cachedEtag) && !string.IsNullOrEmpty(cachedEtag))
                request.Headers.TryAddWithoutValidation("If-None-Match", cachedEtag);

            var response = await client.SendAsync(request, ct);

            // 304 Not Modified: nothing in this status slice changed since last poll — nothing new
            // to enumerate here. GitHub conditional requests do not count against the primary rate limit.
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                continue;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Skipping one GitHub run query for {Repo}: {StatusCode} ({Url})",
                    repo,
                    response.StatusCode,
                    url);
                continue;
            }

            if (response.Headers.ETag?.Tag is { Length: > 0 } etag)
                _runsEtagCache[url] = etag;

            using var runsDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!runsDoc.RootElement.TryGetProperty("workflow_runs", out var workflowRuns))
                continue;

            foreach (var run in workflowRuns.EnumerateArray())
            {
                if (!run.TryGetProperty("id", out var idProp))
                    continue;

                var runId = idProp.GetInt64().ToString();
                if (runs.ContainsKey(runId))
                    continue;

                var workflowName = run.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? ""
                    : "";
                var jobsUrl = run.TryGetProperty("jobs_url", out var jobsUrlProp)
                    ? jobsUrlProp.GetString() ?? ""
                    : "";

                runs[runId] = new GitHubWorkflowRunRef(runId, workflowName, jobsUrl);
            }
        }

        return runs.Values.ToList();
    }

    private static IEnumerable<string> ResolveReposToPoll(ProvisioningRule rule, ProviderCredential credential)
    {
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetOwners = GitHubCredentialResolver.GetTargetOwners(credential).ToList();

        foreach (var allowedRepo in rule.AllowedRepos.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (allowedRepo.Contains('/'))
            {
                repos.Add(allowedRepo);
                continue;
            }

            if (rule.AllowedOrgs.Count > 0)
            {
                foreach (var org in rule.AllowedOrgs.Where(o => !string.IsNullOrWhiteSpace(o)))
                    repos.Add($"{org}/{allowedRepo}");
            }
            else if (targetOwners.Count > 0)
            {
                foreach (var owner in targetOwners)
                    repos.Add($"{owner}/{allowedRepo}");
            }
        }

        foreach (var target in GitHubCredentialResolver.GetConfiguredTargets(credential))
        {
            if (!string.IsNullOrWhiteSpace(target.Repository))
                repos.Add(target.Repository);
        }

        return repos;
    }

    private async void HandleJobQueued(WebhookEvent evt, string profileId)
    {
        try
        {
            _logger.LogInformation("HandleJobQueued fired: repo={Repo}, jobId={JobId}, profileId={ProfileId}",
                evt.Repository, evt.JobId, profileId);
            await HandleJobQueuedAsync(evt, profileId, isRecoveryAttempt: false);
            TriggerQueueSweep();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error provisioning runner for webhook event {EventId}", evt.Id);
        }
    }

    private async Task ProcessPendingWebhookEventsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var now = DateTime.UtcNow;

        await RecoverProvisionedWebhookEventsAsync(store, now, ct);

        var pendingEvents = (await store.Query<WebhookEvent>().ToList())
            .Where(e => e.Action == "queued" && (e.IsRetryCandidate(now) || e.HasExpired(now)))
            .OrderBy(e => e.ReceivedAt)
            .ThenBy(e => e.Id)
            .ToList();

        foreach (var pendingEvent in pendingEvents)
        {
            if (ct.IsCancellationRequested)
                break;

            await HandleJobQueuedAsync(
                pendingEvent,
                pendingEvent.MatchedProfileId,
                isRecoveryAttempt: true);
        }
    }

    private async Task RecoverProvisionedWebhookEventsAsync(IDocumentStore store, DateTime now, CancellationToken ct)
    {
        var provisionedEvents = (await store.Query<WebhookEvent>().ToList())
            .Where(e => e.Action == "queued" && string.Equals(e.Status, "provisioned", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.UpdatedAt == default ? e.ReceivedAt : e.UpdatedAt)
            .ToList();

        if (provisionedEvents.Count == 0)
            return;

        var instancesById = (await store.Query<RunnerInstance>().ToList())
            .ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var provisionedEvent in provisionedEvents)
        {
            ct.ThrowIfCancellationRequested();

            instancesById.TryGetValue(provisionedEvent.InstanceId ?? "", out var linkedInstance);
            if (!ShouldRetryProvisionedEvent(provisionedEvent, linkedInstance, out var reason))
                continue;

            provisionedEvent.ScheduleRetry(
                reason,
                now,
                _retrySweepInterval,
                status: "pending",
                countAttempt: false);
            provisionedEvent.InstanceId = null;
            provisionedEvent.ResolvedAt = null;
            await store.Update(provisionedEvent);

            _logger.LogWarning(
                "Recovered stale provisioned webhook event {EventId} for job {JobId}: {Reason}",
                provisionedEvent.Id,
                provisionedEvent.JobId,
                reason);
        }
    }

    private async Task RunQueueSweepAsync(CancellationToken ct)
    {
        if (!await _processingGate.WaitAsync(0, ct))
            return;

        try
        {
            if (DateTime.UtcNow - _lastGitHubPollAt >= _githubPollInterval)
            {
                await ReconcileQueuedGitHubJobsAsync(ct);
                _lastGitHubPollAt = DateTime.UtcNow;
            }

            await ProcessPendingWebhookEventsAsync(ct);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private void TriggerQueueSweep()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunQueueSweepAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Immediate queue sweep trigger failed");
            }
        });
    }

    internal async Task ProcessQueuedWebhookEventOnceAsync(
        string eventId,
        string? requestedProfileId,
        CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var evt = await store.Get<WebhookEvent>(eventId)
            ?? throw new InvalidOperationException($"Webhook event '{eventId}' was not found.");

        ct.ThrowIfCancellationRequested();
        await HandleJobQueuedAsync(evt, requestedProfileId, isRecoveryAttempt: true);
    }

    private void HandleRunnerStatusChanged(RunnerStatusChangedEvent _)
        => TriggerQueueSweep();

    private void HandleHostStatusChanged(HostStatusChangedEvent _)
        => TriggerQueueSweep();

    private async Task<QueueProcessingOutcome> HandleJobQueuedAsync(
        WebhookEvent evt,
        string? requestedProfileId,
        bool isRecoveryAttempt)
    {
        var jobLockKey = string.IsNullOrWhiteSpace(evt.JobId) ? evt.Id : evt.JobId;
        var gate = _jobLocks.GetOrAdd(jobLockKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var now = DateTime.UtcNow;

            var currentEvent = await store.Get<WebhookEvent>(evt.Id) ?? evt;
            currentEvent.EnsureLifecycleWindow(now, _pendingTimeout);

            // Stop retrying as soon as provider-side state says the job already moved on.
            var relatedEvents = (await store.Query<WebhookEvent>().ToList())
                .Where(e => e.JobId == currentEvent.JobId)
                .ToList();

            if (relatedEvents.Any(e => e.Action == "completed"))
            {
                currentEvent.MarkResolved("completed", now, currentEvent.InstanceId);
                await store.Update(currentEvent);
                return QueueProcessingOutcome.Advanced;
            }

            if (relatedEvents.Any(e => e.Action == "in_progress"))
            {
                currentEvent.MarkResolved("in_progress", now, currentEvent.InstanceId);
                await store.Update(currentEvent);
                return QueueProcessingOutcome.Advanced;
            }

            if (currentEvent.Status is "completed" or "timed_out" or "rejected" or "ignored" or WebhookEvent.StatusIgnoredScope or WebhookEvent.StatusIgnoredTarget or "in_progress")
                return QueueProcessingOutcome.Advanced;

            if (currentEvent.HasExpired(now))
            {
                await MarkWebhookEventTimedOutAsync(store, currentEvent, now);
                return QueueProcessingOutcome.Advanced;
            }

            if (await TryBindExistingInstanceAsync(store, currentEvent, now))
                return QueueProcessingOutcome.Advanced;

            var (rule, profile, matchedRunnerDefinition, profileError) = await ResolveProvisioningMatchAsync(store, currentEvent, requestedProfileId);
            if (profile == null)
            {
                await ScheduleRetryAsync(
                    store,
                    currentEvent,
                    profileError,
                    now,
                    status: "no_match",
                    countAttempt: false,
                    delay: _retrySweepInterval);
                return QueueProcessingOutcome.Blocked;
            }

            if (await HasEarlierQueuedWorkAheadAsync(store, currentEvent, rule, profile))
            {
                await ScheduleRetryAsync(
                    store,
                    currentEvent,
                    "Waiting for earlier queued jobs in the same capacity lane",
                    now,
                    status: "pending_fifo",
                    countAttempt: false,
                    delay: _retrySweepInterval);
                return QueueProcessingOutcome.Blocked;
            }

            currentEvent.BindingId = rule?.Id ?? currentEvent.BindingId;
            currentEvent.MatchedProfileId = profile.Id;
            currentEvent.MatchedProfileName = profile.Name;
            currentEvent.MatchedRunnerDefinitionId = matchedRunnerDefinition?.Id;
            currentEvent.MatchedRunnerDefinitionName = matchedRunnerDefinition?.Name;
            await UpdateEventProgressAsync(
                store,
                currentEvent,
                "matching",
                $"Matched profile '{profile.Name}', looking for a compatible {profile.RequiredHostPlatform} host",
                now);

            if (rule != null && await IsRuleAtCapacityAsync(store, rule, currentEvent.Id, profile.Id))
            {
                await ScheduleRetryAsync(
                    store,
                    currentEvent,
                    $"Provisioning rule '{rule.Name}' is at capacity and will be retried automatically",
                    now,
                    status: "pending_capacity",
                    countAttempt: false,
                    delay: _retrySweepInterval);
                return QueueProcessingOutcome.Blocked;
            }

            ProviderCredential? credential = null;
            if (!string.IsNullOrEmpty(profile.ProviderCredentialId))
            {
                credential = await store.Get<ProviderCredential>(profile.ProviderCredentialId);
                if (credential == null)
                {
                    await ScheduleRetryAsync(
                        store,
                        currentEvent,
                        $"Credential '{profile.ProviderCredentialId}' is missing and will be retried automatically",
                        now,
                        status: "pending_config",
                        countAttempt: false,
                        delay: _retrySweepInterval);
                    return QueueProcessingOutcome.Blocked;
                }
            }

            var hosts = (await store.Query<Host>().ToList()).ToList();
            var instances = (await store.Query<RunnerInstance>().ToList()).ToList();
            var backendName = profile.ExecutionBackend.ToString().ToLowerInvariant();
            var hostSelection = await SelectHostAsync(store, profile, rule, hosts, instances);

            if (hostSelection.Host == null)
            {
                await ScheduleRetryAsync(
                    store,
                    currentEvent,
                    hostSelection.Reason
                    ?? $"No online HostWorker matches platform '{profile.RequiredHostPlatform}' with backend '{backendName}'",
                    now,
                    status: hostSelection.CapacityBlocked ? "pending_capacity" : "pending_host_match",
                    countAttempt: false,
                    delay: _retrySweepInterval);
                return QueueProcessingOutcome.Blocked;
            }

            if (!_hostCommands.CanDispatchToHost(hostSelection.Host.Id))
            {
                await ScheduleRetryAsync(
                    store,
                    currentEvent,
                    BuildHostWorkerDisconnectedReason(hostSelection.Host),
                    now,
                    status: "pending_host_match",
                    countAttempt: false,
                    delay: _retrySweepInterval);
                return QueueProcessingOutcome.Blocked;
            }

            await UpdateEventProgressAsync(
                store,
                currentEvent,
                "preparing",
                $"Matched host '{hostSelection.Host.Label}', preparing runner provisioning",
                now);

            var shortGuid = Guid.NewGuid().ToString("N")[..8];
            // Profile names are free-text display strings (may contain spaces/symbols), but
            // runnerName also becomes a Docker container name downstream, which only allows
            // [a-zA-Z0-9][a-zA-Z0-9_.-]* — an unsanitized name there fails container creation
            // instantly on every attempt, since profile names don't change between retries.
            var runnerNamePrefix = RunnerMetadataBuilder.SanitizeRunnerNameComponent(profile.Name) ?? "runner";
            var runnerName = $"{runnerNamePrefix}-jit-{shortGuid}";

            _logger.LogInformation(
                "Selected HostWorker {HostName} for dynamic runner {RunnerName} (job {JobId}, recovery={Recovery})",
                hostSelection.Host.Name,
                runnerName,
                currentEvent.JobId,
                isRecoveryAttempt);

            JitConfigResult? jitResult = null;

            // Apply webhook-supplied image tag override when permitted. We
            // copy the image configs before mutating so the shared profile
            // document / grain cache stays untouched. Done early so the
            // override label can be added to the runner's label set below —
            // GitHub Actions routes jobs by matching every label in
            // `runs-on`, so the runner must advertise the `rr-image-tag=...`
            // label for the queued job to pick it up.
            var (dockerConfig, tartConfig, appliedTagOverride) =
                ApplyImageTagOverride(profile, currentEvent.ImageTagOverride);

            var effectiveLabels = BuildDynamicRunnerLabels(currentEvent, profile, hostSelection.Host);
            if (!string.IsNullOrEmpty(appliedTagOverride))
            {
                var overrideLabel = $"rr-image-tag={appliedTagOverride}";
                if (!effectiveLabels.Contains(overrideLabel, StringComparer.OrdinalIgnoreCase))
                    effectiveLabels.Add(overrideLabel);
            }
            if (credential != null)
            {
                await UpdateEventProgressAsync(
                    store,
                    currentEvent,
                    "preparing",
                    $"Generating {profile.Provider} JIT configuration for host '{hostSelection.Host.Label}'",
                    now);

                jitResult = profile.Provider switch
                {
                    RunnerProvider.GitHubActions => await _jitConfigService.GenerateGitHubJitConfig(
                        credential, runnerName, effectiveLabels, profile.RunnerGroup, currentEvent.Repository, currentEvent.GitHubInstallationId),
                    RunnerProvider.GiteaActions => await _jitConfigService.GenerateGiteaJitConfig(credential, runnerName),
                    _ => null
                };

                if (jitResult is { Success: false })
                {
                    await ScheduleRetryAsync(
                        store,
                        currentEvent,
                        $"JIT config generation failed: {jitResult.Error}",
                        now);
                    return QueueProcessingOutcome.Blocked;
                }
            }

            var envVars = await ComposeEnvironmentVariablesAsync(store, profile, hostSelection.Host, credential);
            var instanceId = Guid.NewGuid().ToString();
            envVars["RR_INSTANCE_ID"] = instanceId;
            envVars["RR_RUNNER_NAME"] = runnerName;

            // Inform the HostWorker if the profile wants a job-started banner hook
            // installed; the host worker picks the right filesystem path per backend.
            if (profile.EmitJobStartedBanner)
                envVars["RR_HOOK_JOB_STARTED_REQUESTED"] = "1";

            // Seed RR_META_* describing this deployment so the banner and any
            // other consumers can read a consistent metadata bag.
            foreach (var kv in RunnerMetadataBuilder.BuildMetadataEnv(profile, hostSelection.Host, null, instanceId, appliedTagOverride))
                envVars[kv.Key] = kv.Value;

            var runnerGrain = _grainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
            await runnerGrain.Initialize(
                hostSelection.Host.Id,
                profile.Id,
                runnerName,
                "dynamic",
                currentEvent.JobId,
                currentEvent.Id,
                currentEvent.BindingId,
                appliedTagOverride,
                matchedRunnerDefinition?.Id);
            await runnerGrain.MarkStarting("Sending dynamic deploy command to host");

            await UpdateEventProgressAsync(
                store,
                currentEvent,
                "dispatching",
                $"Dispatching runner '{runnerName}' to host '{hostSelection.Host.Label}'",
                now);

            var initSteps = await InitStepResolver.ResolveAsync(
                store, profile, envVars, profile.ExecutionBackend, hostSelection.Host.Platform);

            // Resolve registry credentials for Docker image pulls
            var registryCred = await RegistryCredentialResolver.ResolveAsync(store, dockerConfig, _logger);

            // Resolve runner agent version (for Docker auto-install)
            var agentVersion = profile.RunnerAgentVersion;
            if (string.IsNullOrEmpty(agentVersion) || agentVersion == "latest")
            {
                var versions = (await store.Query<RunnerAgentVersion>().ToList())
                    .Where(v => v.Provider == profile.Provider)
                    .OrderByDescending(v => v.IsLatest)
                    .ThenByDescending(v => v.Version)
                    .ToList();
                agentVersion = versions.FirstOrDefault()?.Version;
            }

            var command = new DeployRunnerCommand
            {
                InstanceId = instanceId,
                ProfileId = profile.Id,
                RunnerName = runnerName,
                Backend = profile.ExecutionBackend,
                Provider = profile.Provider,
                EnvironmentVariables = envVars,
                RunnerAgentVersion = agentVersion,
                DockerConfig = dockerConfig,
                TartConfig = tartConfig,
                Labels = effectiveLabels,
                RunnerGroup = GetEffectiveRunnerGroup(profile, credential),
                Ephemeral = true,
                JitConfig = jitResult?.JitConfig,
                RegistrationToken = jitResult?.RegistrationToken,
                ProvisioningMode = "dynamic",
                RunnerUrl = credential != null ? GetRunnerUrl(credential) : null,
                RunnerBasePath = hostSelection.Host.RunnerBasePath,
                WorkDirectory = hostSelection.Host.WorkDirectory,
                InitSteps = initSteps,
                RegistryUsername = registryCred?.Username,
                RegistryPassword = registryCred?.Password,
                BackendCapacityLimit = CapacityPlanningService.GetBackendLimit(hostSelection.Host, profile.ExecutionBackend)
            };

            currentEvent.MarkResolved("provisioned", now, instanceId);
            currentEvent.LastAttemptAt = now;
            currentEvent.MatchedProfileId = profile.Id;
            currentEvent.MatchedProfileName = profile.Name;
            currentEvent.MatchedRunnerDefinitionId = matchedRunnerDefinition?.Id;
            currentEvent.MatchedRunnerDefinitionName = matchedRunnerDefinition?.Name;
            await store.Update(currentEvent);

            try
            {
                await _hostCommands.DispatchDeployRunnerAsync(hostSelection.Host.Id, command);
                await runnerGrain.UpdateStatusMessage("Dynamic deploy command sent to host");
                await runnerGrain.MarkDeployed();
            }
            catch (Exception ex)
            {
                var hostWorkerDisconnected = IsHostWorkerDisconnectedError(ex);
                await runnerGrain.MarkFailed($"Failed to dispatch dynamic deploy command: {ex.Message}");
                if (credential != null)
                    await _runnerRegistrationCleanupService.TryRemoveRunnerAsync(store, BuildCleanupRunnerInstance(
                        instanceId,
                        hostSelection.Host.Id,
                        profile.Id,
                        runnerName,
                        currentEvent,
                        currentEvent.BindingId,
                        appliedTagOverride,
                        matchedRunnerDefinition?.Id), CancellationToken.None);
                await ScheduleRetryAsync(
                    store,
                    currentEvent,
                    hostWorkerDisconnected
                        ? BuildHostWorkerDisconnectedReason(hostSelection.Host)
                        : $"Failed to dispatch dynamic deploy command: {ex.Message}",
                    now,
                    status: hostWorkerDisconnected ? "pending_host_match" : "pending",
                    countAttempt: !hostWorkerDisconnected,
                    delay: hostWorkerDisconnected ? _retrySweepInterval : null);
                return QueueProcessingOutcome.Blocked;
            }

            _logger.LogInformation(
                "Dynamic runner {RunnerName} dispatched to {HostName} for job {JobId} with labels [{Labels}] (event {EventId}){OverrideDetail}",
                runnerName, hostSelection.Host.Name, currentEvent.JobId, string.Join(", ", effectiveLabels), currentEvent.Id,
                appliedTagOverride != null ? $" with image tag override '{appliedTagOverride}'" : "");
            return QueueProcessingOutcome.Advanced;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Produces copies of the profile's image configs with the override tag
    /// applied when the profile opts in. The profile document itself is never
    /// mutated (it's a shared Orleans-cached object). Returns the applied tag
    /// alongside, or null when no override was applied.
    /// </summary>
    internal static (DockerImageConfig? Docker, TartImageConfig? Tart, string? AppliedTag) ApplyImageTagOverride(
        RunnerProfile profile,
        string? imageTagOverride)
    {
        var docker = profile.DockerConfig;
        var tart = profile.TartConfig;

        if (string.IsNullOrEmpty(imageTagOverride) || !profile.AllowWebhookImageTagOverride)
            return (docker, tart, null);

        string? applied = null;

        if (docker != null)
        {
            docker = new DockerImageConfig
            {
                Id = docker.Id,
                RegistryUrl = docker.RegistryUrl,
                ImageName = docker.ImageName,
                Tag = imageTagOverride,
                PullPolicy = docker.PullPolicy,
                CredentialId = docker.CredentialId
            };
            applied = imageTagOverride;
        }

        if (tart != null)
        {
            tart = new TartImageConfig
            {
                Id = tart.Id,
                RegistryUrl = tart.RegistryUrl,
                ImageName = tart.ImageName,
                Tag = imageTagOverride,
                DiskSizeGb = tart.DiskSizeGb,
                CpuCount = tart.CpuCount,
                MemorySizeGb = tart.MemorySizeGb,
                Display = tart.Display,
                SharedDirs = tart.SharedDirs,
                SshUser = tart.SshUser,
                SshPassword = tart.SshPassword
            };
            applied = imageTagOverride;
        }

        return (docker, tart, applied);
    }

    private static string? GetRunnerUrl(ProviderCredential credential)
    {
        return credential.Provider switch
        {
            RunnerProvider.GitHubActions => GetGitHubRunnerUrl(credential),
            RunnerProvider.GiteaActions => credential.GiteaInstanceUrl?.TrimEnd('/'),
            RunnerProvider.AzureDevOps => credential.AzDoOrgUrl?.TrimEnd('/'),
            _ => null
        };
    }

    internal static string GetEffectiveRunnerGroup(RunnerProfile profile, ProviderCredential? credential)
    {
        if (profile.Provider == RunnerProvider.AzureDevOps
            && (string.IsNullOrWhiteSpace(profile.RunnerGroup) || string.Equals(profile.RunnerGroup, "Default", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(credential?.AzDoPoolName))
        {
            return credential.AzDoPoolName;
        }

        return string.IsNullOrWhiteSpace(profile.RunnerGroup) ? "Default" : profile.RunnerGroup;
    }

    internal static List<string> BuildDynamicRunnerLabels(WebhookEvent evt, RunnerProfile profile)
        => BuildDynamicRunnerLabels(evt, profile, host: null);

    internal static List<string> BuildDynamicRunnerLabels(WebhookEvent evt, RunnerProfile profile, Host? host)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>();

        void AddLabels(IEnumerable<string> source)
        {
            foreach (var label in source)
            {
                var trimmed = label?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
                    continue;

                labels.Add(trimmed);
            }
        }

        AddLabels(evt.Labels);
        AddLabels(profile.Labels);

        if (seen.Add("self-hosted"))
            labels.Insert(0, "self-hosted");

        if (profile.EmitMetadataLabels)
        {
            foreach (var metaLabel in RunnerMetadataBuilder.BuildMetadataLabels(profile, host))
            {
                if (seen.Add(metaLabel))
                    labels.Add(metaLabel);
            }
        }

        return labels;
    }

    internal static bool ShouldRetryProvisionedEvent(
        WebhookEvent evt,
        RunnerInstance? linkedInstance,
        out string reason)
    {
        reason = "";

        if (!string.Equals(evt.Action, "queued", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evt.Status, "provisioned", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(evt.InstanceId))
        {
            reason = "Provisioned event no longer references a runner instance";
            return true;
        }

        if (linkedInstance == null)
        {
            reason = "Provisioned runner record is missing";
            return true;
        }

        if (!string.Equals(linkedInstance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Provisioned event was linked to a non-dynamic runner record";
            return true;
        }

        if (string.IsNullOrWhiteSpace(linkedInstance.JobId)
            || !string.Equals(linkedInstance.JobId, evt.JobId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Provisioned runner record is not linked to the queued provider job";
            return true;
        }

        // Note: linkedInstance.WebhookEventId intentionally isn't compared against evt.Id here.
        // TryBindExistingInstanceAsync binds duplicate "queued" events (webhook redelivery,
        // GitHub poll backfill) to an already-provisioned instance without repointing
        // WebhookEventId at the newer event, so a mismatch here doesn't mean the instance is
        // stale — it's expected for every duplicate event bound to a shared instance. Treating
        // it as staleness bounced those events back to "pending" on every recovery sweep,
        // which re-entered the create-a-new-instance path whenever the shared instance
        // transiently left the active-status set, producing multiple runner instances for one job.

        if (linkedInstance.Status is RunnerInstanceStatus.Stopped
            or RunnerInstanceStatus.Failed
            or RunnerInstanceStatus.Crashed)
        {
            reason = $"Provisioned runner is no longer active ({linkedInstance.Status})";
            return true;
        }

        return false;
    }

    private static string? GetGitHubRunnerUrl(ProviderCredential credential)
        => GitHubCredentialResolver.GetRunnerUrl(credential);

    private static string BuildHostWorkerDisconnectedReason(Host host)
        => $"HostWorker '{host.Label}' is not connected; waiting for it to reconnect before generating a runner JIT config.";

    private static bool IsHostWorkerDisconnectedError(Exception ex)
        => ex is InvalidOperationException && ex.Message.Contains("is not connected", StringComparison.OrdinalIgnoreCase);

    private static RunnerInstance BuildCleanupRunnerInstance(
        string instanceId,
        string hostId,
        string profileId,
        string runnerName,
        WebhookEvent evt,
        string? provisioningRuleId,
        string? imageTagOverride,
        string? runnerDefinitionId)
        => new()
        {
            Id = instanceId,
            HostId = hostId,
            ProfileId = profileId,
            RunnerName = runnerName,
            ProvisioningMode = "dynamic",
            JobId = evt.JobId,
            WebhookEventId = evt.Id,
            ProvisioningRuleId = provisioningRuleId,
            ImageTagOverride = imageTagOverride,
            RunnerDefinitionId = runnerDefinitionId,
            ManagedByRunnerRunner = true
        };

    private async Task<Dictionary<string, string>> ComposeEnvironmentVariablesAsync(
        IDocumentStore store, RunnerProfile profile, Host host, ProviderCredential? credential)
    {
        var result = new Dictionary<string, string>();

        // Layer 0: Provider credential vars (RR_ prefixed)
        if (credential != null)
            InjectCredentialVars(result, credential);

        // Layer 1: Environment variable sets (ordered by priority)
        var allSets = (await store.Query<EnvironmentVariableSet>().ToList()).ToList();
        var selectedSets = allSets
            .Where(s => profile.EnvironmentVariableSetIds.Contains(s.Id))
            .OrderBy(s => s.Priority)
            .ToList();

        foreach (var set in selectedSets)
            foreach (var kvp in set.Variables)
                result[kvp.Key] = kvp.Value;

        // Layer 2: Profile-level overrides
        foreach (var kvp in profile.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        // Layer 3: Host-level overrides
        if (host.EnvironmentOverrides != null)
            foreach (var kvp in host.EnvironmentOverrides)
                result[kvp.Key] = kvp.Value;

        // Expand $RR_* variable references
        ExpandVariableReferences(result);

        return result;
    }

    private static void InjectCredentialVars(Dictionary<string, string> vars, ProviderCredential cred)
    {
        switch (cred.Provider)
        {
            case RunnerProvider.GitHubActions:
                var target = GitHubCredentialResolver.ResolveDefaultTarget(cred);
                if (!string.IsNullOrEmpty(cred.GitHubToken)) vars["RR_GITHUB_TOKEN"] = cred.GitHubToken;
                if (!string.IsNullOrEmpty(target?.Owner)) vars["RR_GITHUB_ORG"] = target.Owner;
                if (!string.IsNullOrEmpty(target?.Repository)) vars["RR_GITHUB_REPO"] = target.Repository;
                if (!string.IsNullOrEmpty(cred.GitHubApiUrl)) vars["RR_GITHUB_API_URL"] = cred.GitHubApiUrl;
                if (!string.IsNullOrEmpty(cred.GitHubServerUrl)) vars["RR_GITHUB_SERVER_URL"] = cred.GitHubServerUrl;
                break;
            case RunnerProvider.GiteaActions:
                if (!string.IsNullOrEmpty(cred.GiteaRunnerToken)) vars["RR_GITEA_RUNNER_TOKEN"] = cred.GiteaRunnerToken;
                if (!string.IsNullOrEmpty(cred.GiteaInstanceUrl)) vars["RR_GITEA_INSTANCE_URL"] = cred.GiteaInstanceUrl;
                break;
            case RunnerProvider.AzureDevOps:
                if (!string.IsNullOrEmpty(cred.AzDoPat)) vars["RR_AZDO_PAT"] = cred.AzDoPat;
                if (!string.IsNullOrEmpty(cred.AzDoOrgUrl)) vars["RR_AZDO_ORG_URL"] = cred.AzDoOrgUrl;
                if (!string.IsNullOrEmpty(cred.AzDoProjectName)) vars["RR_AZDO_PROJECT"] = cred.AzDoProjectName;
                if (!string.IsNullOrEmpty(cred.AzDoPoolName)) vars["RR_AZDO_POOL"] = cred.AzDoPoolName;
                break;
        }
    }

    private static void ExpandVariableReferences(Dictionary<string, string> vars)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            foreach (var key in vars.Keys.ToList())
            {
                var value = vars[key];
                if (!value.Contains('$')) continue;

                var expanded = value;
                foreach (var refKey in vars.Keys)
                {
                    expanded = expanded
                        .Replace($"${{{refKey}}}", vars[refKey])
                        .Replace($"${refKey}", vars[refKey]);
                }

                if (expanded != value)
                {
                    vars[key] = expanded;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    private async Task ScheduleRetryAsync(
        IDocumentStore store,
        WebhookEvent evt,
        string reason,
        DateTime now,
        string status = "pending",
        bool countAttempt = true,
        TimeSpan? delay = null)
    {
        evt.EnsureLifecycleWindow(now, _pendingTimeout);
        if (evt.HasExpired(now))
        {
            await MarkWebhookEventTimedOutAsync(store, evt, now);
            return;
        }

        evt.ResolvedAt = null;
        evt.InstanceId = null;
        evt.ScheduleRetry(reason, now, delay ?? ComputeRetryDelay(evt.RetryCount), status, countAttempt);
        await store.Update(evt);

        _logger.LogInformation(
            "Queued webhook event {EventId} for retry #{RetryCount} at {NextRetry}: {Reason}",
            evt.Id, evt.RetryCount, evt.NextRetryAt, reason);
    }

    private async Task UpdateEventProgressAsync(
        IDocumentStore store,
        WebhookEvent evt,
        string status,
        string detail,
        DateTime now)
    {
        evt.EnsureLifecycleWindow(now, _pendingTimeout);
        evt.SetProgress(status, detail, now, now.Add(_retrySweepInterval));
        await store.Update(evt);
    }

    private TimeSpan ComputeRetryDelay(int priorRetries)
    {
        var baseSeconds = Math.Max(5, (int)_retrySweepInterval.TotalSeconds);
        var backoffMultiplier = Math.Min(1 << Math.Min(priorRetries, 3), 8);
        return TimeSpan.FromSeconds(Math.Min(baseSeconds * backoffMultiplier, 120));
    }

    private static async Task<(ProvisioningRule? Rule, RunnerProfile? Profile, RunnerDefinition? RunnerDefinition, string Reason)> ResolveProvisioningMatchAsync(
        IDocumentStore store,
        WebhookEvent evt,
        string? requestedProfileId)
    {
        if (!Enum.TryParse<RunnerProvider>(evt.Provider, true, out var provider))
            return (null, null, null, $"Unsupported provider '{evt.Provider}'");

        var repo = evt.Repository;
        var org = repo.Contains('/') ? repo.Split('/')[0] : "";

        var allRules = (await store.Query<ProvisioningRule>().ToList())
            .Where(r => r.Type == ProvisioningType.Webhook && r.Provider == provider && r.Enabled)
            .ToList();

        var candidateRules = allRules
            .Where(r =>
                r.AllowedRepos.Any(x => x.Equals(repo, StringComparison.OrdinalIgnoreCase))
                || r.AllowedOrgs.Any(x => x.Equals(org, StringComparison.OrdinalIgnoreCase))
                || (r.AllowedRepos.Count == 0 && r.AllowedOrgs.Count == 0))
            .OrderByDescending(r => r.Id == evt.BindingId)
            .ToList();

        if (candidateRules.Count == 0)
            return (null, null, null, $"No provisioning rule currently matches repository '{repo}'");

        foreach (var rule in candidateRules)
        {
            evt.RequestedRunnerTargetKey = rule.ResolveRequestedTargetKey(evt.Labels);
            evt.ValidRunnerTargetKeys = rule.GetValidRunnerTargetKeys();

            RunnerDefinition? runnerDefinition;
            RunnerProfile? profile;
            try
            {
                (runnerDefinition, profile) = await ProvisioningRuleRunnerResolver.ResolveProfileAsync(
                    store,
                    rule,
                    evt.Labels,
                    requestedProfileId);
            }
            catch (InvalidOperationException ex)
            {
                evt.RunnerTargetSelectionReason = ex.Message;
                return (rule, null, null, ex.Message);
            }

            if (profile == null)
            {
                evt.RunnerTargetSelectionReason = rule.RunnerDefinitions.Count > 0
                    ? rule.BuildNoRunnerTargetMatchReason(evt.Labels)
                    : $"No current label mapping matches labels [{string.Join(", ", evt.Labels)}]";
                continue;
            }

            evt.RunnerTargetSelectionReason = runnerDefinition == null
                ? "Matched legacy profile mapping"
                : $"Selected runner target '{runnerDefinition.TargetKey}'";
            return (rule, profile, runnerDefinition, "");
        }

        var fallbackRule = candidateRules[0];
        var reason = fallbackRule.RunnerDefinitions.Count > 0
            ? fallbackRule.BuildNoRunnerTargetMatchReason(evt.Labels)
            : $"No current label mapping matches labels [{string.Join(", ", evt.Labels)}]";
        evt.RequestedRunnerTargetKey = fallbackRule.ResolveRequestedTargetKey(evt.Labels);
        evt.ValidRunnerTargetKeys = fallbackRule.GetValidRunnerTargetKeys();
        evt.RunnerTargetSelectionReason = reason;
        return (fallbackRule, null, null, reason);
    }

    private static async Task<bool> IsRuleAtCapacityAsync(
        IDocumentStore store,
        ProvisioningRule rule,
        string currentEventId,
        string profileId)
    {
        var hosts = (await store.Query<Host>().ToList()).ToList();
        var allRules = (await store.Query<ProvisioningRule>().ToList()).ToList();
        var profiles = (await store.Query<RunnerProfile>().ToList())
            .ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        ProvisioningRuleRunnerResolver.AddMaterializedRunnerProfiles(profiles, allRules);
        var instances = (await store.Query<RunnerInstance>().ToList()).ToList();
        var events = (await store.Query<WebhookEvent>().ToList()).ToList();

        var view = CapacityPlanningService.EvaluateRuleCapacity(rule, hosts, profiles, instances, events);
        return view.RemainingSlots <= 0;
    }

    private static async Task<bool> HasEarlierQueuedWorkAheadAsync(
        IDocumentStore store,
        WebhookEvent currentEvent,
        ProvisioningRule? currentRule,
        RunnerProfile currentProfile)
    {
        var rules = (await store.Query<ProvisioningRule>().ToList())
            .ToDictionary(r => r.Id, r => r, StringComparer.OrdinalIgnoreCase);
        var profiles = (await store.Query<RunnerProfile>().ToList())
            .ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        ProvisioningRuleRunnerResolver.AddMaterializedRunnerProfiles(profiles, rules.Values);
        var events = (await store.Query<WebhookEvent>().ToList()).ToList();

        return CapacityPlanningService.HasEarlierQueuedWorkAhead(
            currentEvent,
            currentRule,
            currentProfile,
            events,
            rules,
            profiles);
    }

    private static async Task<HostSelectionResult> SelectHostAsync(
        IDocumentStore store,
        RunnerProfile profile,
        ProvisioningRule? rule,
        List<Host> hosts,
        List<RunnerInstance> instances)
    {
        var profilesById = (await store.Query<RunnerProfile>().ToList())
            .ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        var rules = (await store.Query<ProvisioningRule>().ToList()).ToList();
        ProvisioningRuleRunnerResolver.AddMaterializedRunnerProfiles(profilesById, rules);
        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            rule,
            hosts,
            profilesById,
            instances,
            requireDispatchReadiness: true);

        if (analysis.SelectedHost != null)
            return new HostSelectionResult(analysis.SelectedHost, null, false);

        if (analysis.CapacityBlocked)
            return new HostSelectionResult(null, analysis.Reason, true);

        return new HostSelectionResult(null, analysis.Reason, false);
    }

    private static bool MatchesRuleHostRequirements(Host host, ProvisioningRule? rule)
        => CapacityPlanningService.MatchesRuleHostRequirements(host, rule);

    private async Task<bool> TryBindExistingInstanceAsync(IDocumentStore store, WebhookEvent evt, DateTime now)
    {
        var instances = (await store.Query<RunnerInstance>().ToList())
            .Where(i => i.ProvisioningMode == "dynamic" && i.JobId == evt.JobId)
            .OrderByDescending(i => i.CreatedAt)
            .ToList();

        var activeInstance = instances.FirstOrDefault(i =>
            i.Status is RunnerInstanceStatus.Running
                or RunnerInstanceStatus.Starting
                or RunnerInstanceStatus.Pending
                or RunnerInstanceStatus.Stopping);

        if (activeInstance == null)
            return false;

        evt.MarkResolved("provisioned", now, activeInstance.Id);
        evt.LastAttemptAt ??= now;
        await store.Update(evt);

        return true;
    }

    private async Task MarkWebhookEventTimedOutAsync(IDocumentStore store, WebhookEvent evt, DateTime now)
    {
        var reason = $"Timed out waiting for a runner to start within {_pendingTimeout.TotalMinutes:0} minute(s)";
        evt.Status = "timed_out";
        evt.Error = reason;
        evt.ResolvedAt = now;
        evt.NextRetryAt = null;
        await store.Update(evt);

        await TryCancelProviderJobAsync(store, evt, reason);
        await CleanupDynamicRunnersForJobAsync(store, evt.JobId, reason, removeRecords: true);

        _logger.LogWarning("Webhook event {EventId} for job {JobId} timed out", evt.Id, evt.JobId);
    }

    private async Task TryCancelProviderJobAsync(IDocumentStore store, WebhookEvent evt, string reason)
    {
        if (!string.Equals(evt.Provider, RunnerProvider.GitHubActions.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(evt.RunId)
            || string.IsNullOrWhiteSpace(evt.Repository)
            || !evt.Repository.Contains('/'))
        {
            return;
        }

        ProviderCredential? credential = null;

        if (!string.IsNullOrWhiteSpace(evt.MatchedProfileId))
        {
            var profile = await store.Get<RunnerProfile>(evt.MatchedProfileId);
            if (!string.IsNullOrWhiteSpace(profile?.ProviderCredentialId))
                credential = await store.Get<ProviderCredential>(profile.ProviderCredentialId);
        }

        if (credential == null && !string.IsNullOrWhiteSpace(evt.BindingId))
        {
            var rule = await store.Get<ProvisioningRule>(evt.BindingId);
            if (!string.IsNullOrWhiteSpace(rule?.ProviderCredentialId))
                credential = await store.Get<ProviderCredential>(rule.ProviderCredentialId);
        }

        if (credential == null || !GitHubAuthenticationService.HasGitHubApiCredentials(credential, evt.GitHubInstallationId))
            return;

        try
        {
            var apiUrl = credential.GitHubApiUrl?.TrimEnd('/') ?? "https://api.github.com";
            using var client = _httpClientFactory.CreateClient();
            await _gitHubAuth.ConfigureClientAsync(client, credential, evt.GitHubInstallationId);

            using var response = await client.PostAsync(
                $"{apiUrl}/repos/{evt.Repository}/actions/runs/{evt.RunId}/cancel",
                new StringContent(string.Empty));

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Requested cancellation for timed-out GitHub workflow run {RunId} ({Repo}): {Reason}",
                    evt.RunId,
                    evt.Repository,
                    reason);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Failed to cancel timed-out GitHub workflow run {RunId} ({Repo}): {StatusCode} {Body}",
                    evt.RunId,
                    evt.Repository,
                    response.StatusCode,
                    body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error requesting cancellation for timed-out GitHub workflow run {RunId} ({Repo})",
                evt.RunId,
                evt.Repository);
        }
    }

    private async void HandleJobCompleted(string jobId, string conclusion)
    {
        try
        {
            _logger.LogInformation("Job {JobId} completed ({Conclusion}), looking for dynamic runner to clean up",
                jobId, conclusion);

            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            var queuedEvents = (await store.Query<WebhookEvent>().ToList())
                .Where(e => e.Action == "queued" && e.JobId == jobId)
                .ToList();

            var now = DateTime.UtcNow;
            foreach (var queuedEvent in queuedEvents)
            {
                queuedEvent.MarkResolved("completed", now, queuedEvent.InstanceId);
                await store.Update(queuedEvent);
            }

            await CleanupDynamicRunnersForJobAsync(store, jobId, $"Job completed ({conclusion})", removeRecords: true);
            TriggerQueueSweep();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up dynamic runner for job {JobId}", jobId);
        }
    }

    private async Task CleanupDynamicRunnersForJobAsync(
        IDocumentStore store,
        string jobId,
        string reason,
        bool removeRecords)
    {
        var instances = (await store.Query<RunnerInstance>().ToList())
            .Where(i => i.ProvisioningMode == "dynamic" && i.JobId == jobId)
            .ToList();

        if (!instances.Any())
        {
            _logger.LogDebug("No dynamic runner found for job {JobId}", jobId);
            return;
        }

        foreach (var instance in instances)
        {
            _logger.LogInformation(
                "Cleaning up dynamic runner {RunnerName} for job {JobId}: {Reason}",
                instance.RunnerName, jobId, reason);

            var host = (await store.Query<Host>().ToList()).FirstOrDefault(h => h.Id == instance.HostId);
            if (host != null)
            {
                await _hostCommands.DispatchStopRunnerAsync(host.Id, new StopRunnerCommand
                {
                    InstanceId = instance.Id,
                    InstanceHandle = instance.ContainerId ?? instance.VmName ?? instance.ProcessId?.ToString()
                });
            }

            await _runnerRegistrationCleanupService.TryRemoveRunnerAsync(store, instance);

            instance.Status = RunnerInstanceStatus.Stopped;
            instance.ErrorMessage = reason;
            instance.StoppedAt = DateTime.UtcNow;
            await store.Update(instance);

            if (removeRecords)
                await store.Remove<RunnerInstance>(instance.Id);
        }
    }
}
