using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orleans.Concurrency;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
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
        var labels = workflowJob.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString() ?? "").Where(l => l.Length > 0).ToList();
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
        ProvisioningRule? matchedRule = null;
        var hmacMatchCount = 0;
        foreach (var rule in candidateRules)
        {
            if (string.IsNullOrEmpty(rule.WebhookSecret))
                continue;

            if (!ValidateHmac(body, rule.WebhookSecret, signatureHeader, provider))
                continue;

            hmacMatchCount++;

            // Check repo/org scope
            var repoMatch = rule.AllowedRepos.Any(r =>
                r.Equals(repo, StringComparison.OrdinalIgnoreCase));
            var orgMatch = rule.AllowedOrgs.Any(o =>
                o.Equals(org, StringComparison.OrdinalIgnoreCase));
            var scopeOpen = rule.AllowedRepos.Count == 0 && rule.AllowedOrgs.Count == 0;

            if (repoMatch || orgMatch || scopeOpen)
            {
                matchedRule = rule;
                break;
            }
        }

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
            var instances = (await store.Query<RunnerInstance>().ToList())
                .Where(i => i.ProvisioningMode == "dynamic" && i.JobId == jobId)
                .ToList();

            string? instanceId = null;
            foreach (var inst in instances)
            {
                var instanceGrain = GrainFactory.GetGrain<IRunnerInstanceGrain>(inst.Id);
                await instanceGrain.MarkRunning(statusMessage: "Job in progress");
                instanceId ??= inst.Id;
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
            return new WebhookProcessResult { Success = true, Status = "completed", Message = "Job completed, cleanup triggered" };
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
                    Status = "no_match"
                });

                return new WebhookProcessResult { Success = false, Status = "no_match", Message = "No profile matched" };
            }

            // Resolve profile name for audit
            var profileGrain = GrainFactory.GetGrain<IProfileGrain>(profileId);
            var profile = await profileGrain.GetProfile();
            var profileName = profile?.Name;

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
                Status = "provisioned"
            };
            await store.Insert(webhookEvent);

            await ruleGrain.HandleWebhookEvent(jobId, repo, labels, jitConfig: null);

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
