using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orleans.Concurrency;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
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
        var runnerProvider = provider switch
        {
            "github" => RunnerProvider.GitHubActions,
            "gitea" => RunnerProvider.GiteaActions,
            _ => (RunnerProvider?)null
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
        var githubInstallationId = ExtractGitHubInstallationId(provider, json);

        var org = repo.Contains('/') ? repo.Split('/')[0] : "";

        // Load all enabled Webhook provisioning rules
        var allRules = await store.Query<ProvisioningRule>().ToList();
        var candidateRules = allRules
            .Where(r => r.Enabled
                && r.Type == ProvisioningType.Webhook
                && (!runnerProvider.HasValue || r.Provider == runnerProvider.Value))
            .ToList();
        var credentialIds = candidateRules
            .Select(r => r.ProviderCredentialId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var credentialsById = new Dictionary<string, ProviderCredential>(StringComparer.Ordinal);
        foreach (var credentialId in credentialIds)
        {
            var credential = await store.Get<ProviderCredential>(credentialId!);
            if (credential != null)
                credentialsById[credential.Id] = credential;
        }

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
            credentialsById.TryGetValue(rule.ProviderCredentialId ?? "", out var credential);
            var webhookSecret = ResolveWebhookSecret(rule, credential, runnerProvider);

            if (string.IsNullOrEmpty(webhookSecret))
                continue;

            if (!ValidateHmac(body, webhookSecret, signatureHeader, provider))
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
                GitHubInstallationId = githubInstallationId,
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
                GitHubInstallationId = githubInstallationId,
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
                GitHubInstallationId = githubInstallationId,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "completed"
            });

            _logger.LogInformation("Job {JobId} completed for webhook rule {RuleId}",
                jobId, matchedRule.Id);
            return new WebhookProcessResult { Success = true, Status = "completed", Message = "Job completed" };
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
                    GitHubInstallationId = githubInstallationId,
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
                GitHubInstallationId = githubInstallationId,
                WorkflowName = workflowName,
                Labels = labels,
                MatchedProfileId = profileId,
                MatchedProfileName = profileName,
                Status = "provisioned",
                ImageTagOverride = effectiveOverride,
                ImageTagOverrideRejectedReason = effectiveRejection
            };
            await store.Insert(webhookEvent);

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
            GitHubInstallationId = githubInstallationId,
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

    private static string? ResolveWebhookSecret(
        ProvisioningRule rule,
        ProviderCredential? credential,
        RunnerProvider? provider)
    {
        if (!string.IsNullOrWhiteSpace(rule.WebhookSecret))
            return rule.WebhookSecret;

        if (provider == RunnerProvider.GitHubActions
            && GitHubAuthenticationService.IsGitHubAppCredential(credential))
        {
            return credential?.GitHubAppWebhookSecret;
        }

        return null;
    }

    private static string? ExtractGitHubInstallationId(string provider, JsonElement json)
    {
        if (provider != "github"
            || !json.TryGetProperty("installation", out var installation)
            || !installation.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetInt64().ToString(),
            JsonValueKind.String => id.GetString(),
            _ => null
        };
    }
}
