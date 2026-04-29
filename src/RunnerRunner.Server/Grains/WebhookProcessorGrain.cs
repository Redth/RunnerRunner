using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orleans.Concurrency;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Webhooks;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Grains;

[StatelessWorker(4)]
public class WebhookProcessorGrain : Grain, IWebhookProcessorGrain
{
    private readonly ILogger<WebhookProcessorGrain> _logger;
    private readonly IServiceProvider _serviceProvider;

    public WebhookProcessorGrain(
        ILogger<WebhookProcessorGrain> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<WebhookProcessResult> ProcessWebhook(string provider, string body, string? signatureHeader)
    {
        // Map HMAC provider key to RunnerProvider enum name for storage
        var providerName = provider switch
        {
            "github" => nameof(RunnerProvider.GitHubActions),
            "gitea" => nameof(RunnerProvider.GiteaActions),
            _ => provider
        };

        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        // Parse body
        JsonElement json;
        try
        {
            json = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON body in webhook");
            return new WebhookProcessResult { Success = false, Status = "error", Message = "Invalid JSON body" };
        }

        // Extract fields
        var action = json.GetProperty("action").GetString() ?? "";
        var workflowJob = json.GetProperty("workflow_job");
        var jobId = workflowJob.GetProperty("id").GetInt64().ToString();
        var runId = workflowJob.GetProperty("run_id").GetInt64().ToString();
        // GitHub populates runner_name on in_progress / completed payloads with the
        // actual runner that GitHub bound to the job. We use it to correct any
        // mis-binding between our dispatch-time intent and the runner GitHub picked.
        var runnerName = workflowJob.TryGetProperty("runner_name", out var rn)
            ? rn.ValueKind == JsonValueKind.String ? rn.GetString() : null
            : null;
        var rawLabels = workflowJob.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString() ?? "").Where(l => l.Length > 0).ToList();

        // Strip recognized magic labels (e.g. rr-image-tag=...) so they don't
        // pollute profile label-mapping comparisons or end up on the runner.
        // The raw labels are still audited on the persisted WebhookEvent via
        // the cleaned list (we don't need the unfiltered set once the magic
        // bits are lifted into dedicated fields).
        var magic = WebhookLabelParser.Extract(rawLabels);
        var labels = magic.CleanLabels;
        var imageTagOverride = magic.ImageTagOverride;
        var imageTagOverrideRejectedReason = magic.ImageTagOverrideRejectedReason;
        var workflowName = workflowJob.TryGetProperty("workflow_name", out var wn)
            ? wn.GetString() ?? "" : "";
        var repo = json.GetProperty("repository").GetProperty("full_name").GetString() ?? "";

        var org = repo.Contains('/') ? repo.Split('/')[0] : "";

        // Load all enabled Webhook provisioning rules
        var allRules = await store.Query<ProvisioningRule>().ToList();
        var candidateRules = allRules
            .Where(r => r.Enabled && r.Type == ProvisioningType.Webhook)
            .ToList();

        // Find a rule where HMAC signature matches AND repo/org is in scope.
        // Multiple rules may share the same webhook secret, so we must check all
        // of them rather than stopping at the first HMAC match.
        // Most-specific match wins: explicit repo > org > open scope.
        ProvisioningRule? matchedRule = null;
        ProvisioningRule? repoMatchRule = null;
        ProvisioningRule? orgMatchRule = null;
        ProvisioningRule? openScopeRule = null;
        var hmacMatchCount = 0;
        foreach (var rule in candidateRules)
        {
            if (string.IsNullOrEmpty(rule.WebhookSecret))
                continue;

            if (!ValidateHmac(body, rule.WebhookSecret, signatureHeader, provider))
                continue;

            hmacMatchCount++;

            // Check repo/org scope — classify by specificity.
            // AllowedRepos may store full names ("org/repo") or short names ("repo").
            var repoShortName = repo.Contains('/') ? repo.Split('/')[1] : repo;
            var repoMatch = rule.AllowedRepos.Any(r =>
                r.Contains('/')
                    ? r.Equals(repo, StringComparison.OrdinalIgnoreCase)
                    : r.Equals(repoShortName, StringComparison.OrdinalIgnoreCase));
            var orgMatch = rule.AllowedOrgs.Any(o =>
                o.Equals(org, StringComparison.OrdinalIgnoreCase));
            var scopeOpen = rule.AllowedRepos.Count == 0 && rule.AllowedOrgs.Count == 0;

            if (repoMatch)
                repoMatchRule ??= rule;
            else if (orgMatch)
                orgMatchRule ??= rule;
            else if (scopeOpen)
                openScopeRule ??= rule;
        }

        // Prefer the most specific scope match
        matchedRule = repoMatchRule ?? orgMatchRule ?? openScopeRule;

        if (matchedRule == null)
        {
            var status = hmacMatchCount > 0 ? "rejected" : (candidateRules.Count > 0 ? "rejected" : "no_match");
            var error = hmacMatchCount > 0
                ? "Repository not in scope"
                : (candidateRules.Count > 0 ? "Signature validation failed" : null);
            var message = hmacMatchCount > 0
                ? $"Repository not in scope (checked {hmacMatchCount} HMAC-matched rules)"
                : (candidateRules.Count > 0 ? "Signature validation failed" : "No matching rule");

            _logger.LogWarning("Webhook from {Repo}: {Message} (checked {Count} rules)",
                repo, message, candidateRules.Count);

            await store.Insert(new WebhookEvent
            {
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = status,
                Error = error
            });

            return new WebhookProcessResult
            {
                Success = false,
                Status = status,
                Message = message
            };
        }

        var ruleGrain = GrainFactory.GetGrain<IProvisioningRuleGrain>(matchedRule.Id);

        // Handle "in_progress"
        if (action == "in_progress")
        {
            var now = DateTime.UtcNow;
            var allInstances = (await store.Query<RunnerInstance>().ToList()).ToList();
            var allEvents = (await store.Query<WebhookEvent>().ToList()).ToList();

            // ROOT CAUSE FIX: When multiple JIT runners are dispatched with the same
            // labels, GitHub may bind a runner to a different job than the one we
            // intended at dispatch. Correct the binding here, before any cleanup or
            // timeout logic runs against the (now-stale) JobId field.
            await RebindRunnerInstanceForJobAsync(store, allInstances, allEvents, runnerName, jobId, now);

            // After rebinding, the instance for this job is identifiable by JobId.
            var instances = allInstances
                .Where(i => i.ProvisioningMode == "dynamic" && i.JobId == jobId)
                .ToList();

            string? instanceId = null;
            foreach (var inst in instances)
            {
                var instanceGrain = GrainFactory.GetGrain<IRunnerInstanceGrain>(inst.Id);
                await instanceGrain.MarkRunning(statusMessage: "Job in progress");
                instanceId ??= inst.Id;
            }

            var queuedEvents = allEvents
                .Where(e => e.Action == "queued" && e.JobId == jobId)
                .ToList();
            foreach (var evt in MarkQueuedEventsInProgress(queuedEvents, instances, now))
                await store.Update(evt);

            await store.Insert(new WebhookEvent
            {
                BindingId = matchedRule.Id,
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "in_progress",
                MatchedProfileId = instances.FirstOrDefault()?.ProfileId,
                InstanceId = instanceId
            });

            _logger.LogInformation("Job {JobId} in progress, runner status updated via grain", jobId);
            return new WebhookProcessResult
            {
                Success = true,
                Status = "in_progress",
                Message = "Job in progress acknowledged",
                InstanceId = instanceId
            };
        }

        // Handle "completed"
        if (action == "completed")
        {
            // Rebind on completed too: a fast job may complete before its
            // in_progress webhook caused us to correct the binding (or it may
            // have been missed). Without this, completion cleanup uses the
            // dispatch-time intended JobId and stops the wrong runner.
            if (!string.IsNullOrWhiteSpace(runnerName))
            {
                var allInstancesC = (await store.Query<RunnerInstance>().ToList()).ToList();
                var allEventsC = (await store.Query<WebhookEvent>().ToList()).ToList();
                await RebindRunnerInstanceForJobAsync(
                    store, allInstancesC, allEventsC, runnerName, jobId, DateTime.UtcNow);
            }

            await store.Insert(new WebhookEvent
            {
                BindingId = matchedRule.Id,
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "completed"
            });

            await ruleGrain.HandleJobCompleted(jobId);

            _logger.LogInformation("Job {JobId} completed, cleanup delegated to ProvisioningRuleGrain {RuleId}",
                jobId, matchedRule.Id);
            return new WebhookProcessResult { Success = true, Status = "completed", Message = "Job completed, cleanup triggered", RunnerName = runnerName };
        }

        // Handle "queued"
        if (action == "queued")
        {
            // Label matching: find profile from rule's label mappings
            var profileId = matchedRule.ResolveWebhookProfileId(labels);

            if (string.IsNullOrEmpty(profileId))
            {
                _logger.LogInformation("No profile match for labels [{Labels}] in rule {RuleName}",
                    string.Join(", ", labels), matchedRule.Name);

                await store.Insert(new WebhookEvent
                {
                    BindingId = matchedRule.Id,
                    Provider = providerName,
                    Action = action,
                    JobId = jobId,
                    RunId = runId,
                    Repository = repo,
                    WorkflowName = workflowName,
                    Labels = labels,
                    Status = "no_match",
                    ImageTagOverride = imageTagOverride,
                    ImageTagOverrideRejectedReason = imageTagOverrideRejectedReason
                });

                return new WebhookProcessResult { Success = false, Status = "no_match", Message = "No profile matched" };
            }

            // Resolve profile name for audit
            var profileGrain = GrainFactory.GetGrain<IProfileGrain>(profileId);
            var profile = await profileGrain.GetProfile();
            var profileName = profile?.Name;

            // Apply opt-in gate for tag override: when the profile didn't opt
            // in, drop the accepted tag and surface a rejection reason in the
            // audit record (so operators can debug "why didn't my override
            // apply"). Invalid tags are rejected regardless of opt-in.
            var effectiveOverride = imageTagOverride;
            var effectiveRejection = imageTagOverrideRejectedReason;
            if (!string.IsNullOrEmpty(imageTagOverride) && profile is { AllowWebhookImageTagOverride: false })
            {
                effectiveOverride = null;
                effectiveRejection ??= "Profile does not have AllowWebhookImageTagOverride enabled";
                _logger.LogInformation(
                    "Webhook supplied image tag '{Tag}' for job {JobId} but profile '{Profile}' does not allow overrides — ignored",
                    imageTagOverride, jobId, profileName ?? profileId);
            }

            var webhookEvent = new WebhookEvent
            {
                BindingId = matchedRule.Id,
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                MatchedProfileId = profileId,
                MatchedProfileName = profileName,
                Status = "provisioned",
                ImageTagOverride = effectiveOverride,
                ImageTagOverrideRejectedReason = effectiveRejection
            };
            await store.Insert(webhookEvent);

            await ruleGrain.HandleWebhookEvent(jobId, repo, labels, jitConfig: null, imageTagOverride: effectiveOverride);

            _logger.LogInformation(
                "Webhook matched: {Repo} job {JobId} -> profile {ProfileName} ({ProfileId}) via rule {RuleId}",
                repo, jobId, profileName ?? "unknown", profileId, matchedRule.Id);

            return new WebhookProcessResult
            {
                Success = true,
                Status = "provisioned",
                Message = "Provisioning requested",
                ProfileId = profileId,
                EventId = webhookEvent.Id
            };
        }

        // Other actions — just log
        await store.Insert(new WebhookEvent
        {
            BindingId = matchedRule.Id,
            Provider = providerName,
            Action = action,
            JobId = jobId,
            RunId = runId,
            Repository = repo,
            WorkflowName = workflowName,
            Labels = labels,
            Status = "ignored"
        });

        return new WebhookProcessResult
        {
            Success = true,
            Status = "ignored",
            Message = $"Action '{action}' ignored"
        };
    }

    /// <summary>
    /// Corrects RunnerInstance ↔ Job binding when GitHub assigns a JIT runner to a
    /// different job than we intended at dispatch. Called on workflow_job
    /// in_progress / completed (the first time the provider tells us which runner
    /// is actually executing the job).
    ///
    /// Without this, fast-completing or 10-min-timed-out jobs cause cleanup to
    /// stop the wrong (still-running) runner.
    /// </summary>
    internal async Task RebindRunnerInstanceForJobAsync(
        IDocumentStore store,
        IReadOnlyList<RunnerInstance> allInstances,
        IReadOnlyList<WebhookEvent> allEvents,
        string? runnerName,
        string jobId,
        DateTime nowUtc)
    {
        var decision = ComputeRebindDecision(allInstances, allEvents, runnerName, jobId);
        if (decision == null)
            return;

        var actualInstance = decision.Instance;
        var oldJobId = actualInstance.JobId;
        var oldEventId = actualInstance.WebhookEventId;
        actualInstance.JobId = jobId;
        if (decision.NewWebhookEventId != null)
            actualInstance.WebhookEventId = decision.NewWebhookEventId;
        await store.Update(actualInstance);

        // Also update the grain state so its persistent state agrees with the
        // RunnerInstance document; otherwise grain-side queries
        // (e.g. ProvisioningRuleGrain.HandleJobCompleted) still see the old JobId.
        try
        {
            var instanceGrain = GrainFactory.GetGrain<IRunnerInstanceGrain>(actualInstance.Id);
            await instanceGrain.RebindJob(jobId, decision.NewWebhookEventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update grain state for rebound instance {InstanceId} ({RunnerName})",
                actualInstance.Id, actualInstance.RunnerName);
        }

        _logger.LogWarning(
            "Rebound runner {RunnerName} (instance {InstanceId}) from job {OldJobId} (event {OldEventId}) to job {NewJobId} (event {NewEventId}); GitHub assigned this runner to a different job than dispatched",
            actualInstance.RunnerName,
            actualInstance.Id,
            oldJobId ?? "(none)",
            oldEventId ?? "(none)",
            jobId,
            decision.NewWebhookEventId ?? "(none)");
    }

    internal sealed record RebindDecision(RunnerInstance Instance, string? NewWebhookEventId);

    /// <summary>
    /// Pure decision function: returns the rebind action to apply, or null if
    /// no rebind is needed. Extracted for testability.
    /// </summary>
    internal static RebindDecision? ComputeRebindDecision(
        IReadOnlyList<RunnerInstance> allInstances,
        IReadOnlyList<WebhookEvent> allEvents,
        string? runnerName,
        string jobId)
    {
        if (string.IsNullOrWhiteSpace(runnerName) || string.IsNullOrWhiteSpace(jobId))
            return null;

        var actualInstance = allInstances.FirstOrDefault(i =>
            i.ProvisioningMode == "dynamic"
            && string.Equals(i.RunnerName, runnerName, StringComparison.OrdinalIgnoreCase));
        if (actualInstance == null)
            return null;

        if (string.Equals(actualInstance.JobId, jobId, StringComparison.Ordinal))
            return null;

        var queuedEventForJob = allEvents
            .Where(e => e.Action == "queued"
                && string.Equals(e.JobId, jobId, StringComparison.Ordinal)
                && e.Status is not "completed" and not "timed_out" and not "rejected" and not "ignored")
            .OrderByDescending(e => e.ReceivedAt)
            .FirstOrDefault();

        return new RebindDecision(actualInstance, queuedEventForJob?.Id);
    }

    internal static IReadOnlyList<WebhookEvent> MarkQueuedEventsInProgress(
        IEnumerable<WebhookEvent> queuedEvents,
        IReadOnlyCollection<RunnerInstance> dynamicInstances,
        DateTime nowUtc)
    {
        var instancesByEventId = dynamicInstances
            .Where(i => !string.IsNullOrWhiteSpace(i.WebhookEventId))
            .GroupBy(i => i.WebhookEventId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var instancesById = dynamicInstances
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var updated = new List<WebhookEvent>();
        foreach (var evt in queuedEvents)
        {
            if (!string.Equals(evt.Action, "queued", StringComparison.OrdinalIgnoreCase)
                || evt.Status is "completed" or "timed_out" or "ignored" or "rejected")
            {
                continue;
            }

            RunnerInstance? linkedInstance = null;
            if (!string.IsNullOrWhiteSpace(evt.Id))
                instancesByEventId.TryGetValue(evt.Id, out linkedInstance);
            if (linkedInstance == null
                && !string.IsNullOrWhiteSpace(evt.InstanceId))
            {
                instancesById.TryGetValue(evt.InstanceId, out linkedInstance);
            }

            evt.MarkResolved("in_progress", nowUtc, linkedInstance?.Id ?? evt.InstanceId);
            if (linkedInstance != null && string.IsNullOrWhiteSpace(evt.MatchedProfileId))
                evt.MatchedProfileId = linkedInstance.ProfileId;
            updated.Add(evt);
        }

        return updated;
    }

    private static bool ValidateHmac(string body, string secret, string? signatureHeader, string provider)
    {
        if (string.IsNullOrEmpty(signatureHeader))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using var hmac = new HMACSHA256(keyBytes);
        var computed = Convert.ToHexStringLower(hmac.ComputeHash(bodyBytes));

        var expected = provider == "github" && signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signatureHeader["sha256=".Length..]
            : signatureHeader;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(expected.ToLowerInvariant()));
    }
}
