using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRunner.Core.Models;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Webhooks;

public static class WebhookEndpoints
{
    /// <summary>
    /// Fired when a "queued" workflow_job event is matched to a profile.
    /// Parameters: (WebhookEvent, profileId)
    /// </summary>
    public static event Action<WebhookEvent, string>? OnJobQueued;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/webhooks/github", async (HttpContext ctx) =>
            await HandleWebhook(ctx, RunnerProvider.GitHubActions));

        endpoints.MapPost("/api/webhooks/gitea", async (HttpContext ctx) =>
            await HandleWebhook(ctx, RunnerProvider.GiteaActions));

        return endpoints;
    }

    private static async Task<IResult> HandleWebhook(HttpContext ctx, RunnerProvider provider)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RunnerRunner.Webhooks");
        var store = ctx.RequestServices.GetRequiredService<IDocumentStore>();

        // Read raw body for signature validation
        ctx.Request.EnableBuffering();
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        ctx.Request.Body.Position = 0;

        // Parse event type header
        var eventType = provider == RunnerProvider.GitHubActions
            ? ctx.Request.Headers["X-GitHub-Event"].FirstOrDefault()
            : ctx.Request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "workflow_job";

        if (eventType != "workflow_job")
            return Results.Ok(new { message = $"Ignored event type: {eventType}" });

        // Parse body
        JsonElement json;
        try
        {
            json = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid JSON body" });
        }

        var action = json.GetProperty("action").GetString() ?? "";
        var workflowJob = json.GetProperty("workflow_job");
        var jobId = workflowJob.GetProperty("id").GetInt64().ToString();
        var runId = workflowJob.GetProperty("run_id").GetInt64().ToString();
        var labels = workflowJob.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString() ?? "").Where(l => l.Length > 0).ToList();
        var workflowName = workflowJob.TryGetProperty("workflow_name", out var wn)
            ? wn.GetString() ?? "" : "";
        var repo = json.GetProperty("repository").GetProperty("full_name").GetString() ?? "";

        // Find matching binding
        var providerName = provider.ToString();
        var bindings = (await store.Query<WebhookBinding>().ToList())
            .Where(b => b.Provider == provider && b.Enabled)
            .ToList();

        var org = repo.Contains('/') ? repo.Split('/')[0] : "";
        WebhookBinding? binding = null;

        foreach (var b in bindings)
        {
            // Check repo-level match first, then org-level
            var repoMatch = b.AllowedRepos.Any(r =>
                r.Equals(repo, StringComparison.OrdinalIgnoreCase));
            var orgMatch = b.AllowedOrgs.Any(o =>
                o.Equals(org, StringComparison.OrdinalIgnoreCase));

            if (repoMatch || orgMatch ||
                (b.AllowedRepos.Count == 0 && b.AllowedOrgs.Count == 0))
            {
                binding = b;
                break;
            }
        }

        if (binding == null)
        {
            logger.LogInformation("No binding found for {Provider} repo {Repo}", providerName, repo);

            await store.Insert(new WebhookEvent
            {
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "no_match"
            });

            return Results.Ok(new { message = "No matching binding" });
        }

        // Validate HMAC-SHA256 signature
        if (!string.IsNullOrEmpty(binding.WebhookSecret))
        {
            var signature = provider == RunnerProvider.GitHubActions
                ? ctx.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                : ctx.Request.Headers["X-Gitea-Signature"].FirstOrDefault();

            if (!ValidateSignature(body, binding.WebhookSecret, signature, provider))
            {
                logger.LogWarning("Invalid webhook signature for binding {BindingName}", binding.Name);

                await store.Insert(new WebhookEvent
                {
                    BindingId = binding.Id,
                    Provider = providerName,
                    Action = action,
                    JobId = jobId,
                    RunId = runId,
                    Repository = repo,
                    WorkflowName = workflowName,
                    Labels = labels,
                    Status = "rejected",
                    Error = "Invalid signature"
                });

                return Results.Unauthorized();
            }
        }

        // Only process "queued" actions
        if (action != "queued")
        {
            await store.Insert(new WebhookEvent
            {
                BindingId = binding.Id,
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "matched"
            });

            return Results.Ok(new { message = $"Action '{action}' acknowledged" });
        }

        // Rate limiting: count active dynamic instances for this binding
        var activeInstances = (await store.Query<RunnerInstance>().ToList())
            .Where(i => i.ProvisioningMode == "dynamic"
                && i.Status is RunnerInstanceStatus.Running
                    or RunnerInstanceStatus.Starting
                    or RunnerInstanceStatus.Pending)
            .ToList();

        // Filter to instances linked to events for this binding
        var bindingEventIds = (await store.Query<WebhookEvent>().ToList())
            .Where(e => e.BindingId == binding.Id)
            .Select(e => e.Id)
            .ToHashSet();

        var activeCount = activeInstances
            .Count(i => i.WebhookEventId != null && bindingEventIds.Contains(i.WebhookEventId));

        if (activeCount >= binding.MaxConcurrentJobs)
        {
            logger.LogWarning("Rate limited: {Count}/{Max} active for binding {BindingName}",
                activeCount, binding.MaxConcurrentJobs, binding.Name);

            await store.Insert(new WebhookEvent
            {
                BindingId = binding.Id,
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "rate_limited"
            });

            return Results.Ok(new { message = "Rate limited", status = "rate_limited" });
        }

        // Label matching: find profile from mappings
        string? profileId = null;
        var sortedMappings = binding.Mappings.OrderByDescending(m => m.Priority);

        foreach (var mapping in sortedMappings)
        {
            if (mapping.RequiredLabels.All(required =>
                labels.Any(l => l.Equals(required, StringComparison.OrdinalIgnoreCase))))
            {
                profileId = mapping.ProfileId;
                break;
            }
        }

        profileId ??= binding.DefaultProfileId;

        if (string.IsNullOrEmpty(profileId))
        {
            logger.LogInformation("No profile match for labels [{Labels}] in binding {BindingName}",
                string.Join(", ", labels), binding.Name);

            await store.Insert(new WebhookEvent
            {
                BindingId = binding.Id,
                Provider = providerName,
                Action = action,
                JobId = jobId,
                RunId = runId,
                Repository = repo,
                WorkflowName = workflowName,
                Labels = labels,
                Status = "no_match"
            });

            return Results.Ok(new { message = "No profile matched" });
        }

        // Resolve profile name for audit
        var profile = await store.Get<RunnerProfile>(profileId);
        var profileName = profile?.Name;

        // Create audit event
        var webhookEvent = new WebhookEvent
        {
            BindingId = binding.Id,
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

        // Fire provisioning event
        logger.LogInformation(
            "Webhook matched: {Repo} job {JobId} -> profile {ProfileName} ({ProfileId})",
            repo, jobId, profileName ?? "unknown", profileId);

        OnJobQueued?.Invoke(webhookEvent, profileId);

        return Results.Ok(new { message = "Provisioning requested", profileId, status = "provisioned" });
    }

    private static bool ValidateSignature(
        string body, string secret, string? signatureHeader, RunnerProvider provider)
    {
        if (string.IsNullOrEmpty(signatureHeader))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(bodyBytes);
        var computed = Convert.ToHexStringLower(hash);

        // GitHub sends "sha256=<hex>", Gitea sends just "<hex>"
        var expected = provider == RunnerProvider.GitHubActions
            ? signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
                ? signatureHeader["sha256=".Length..]
                : signatureHeader
            : signatureHeader;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(expected.ToLowerInvariant()));
    }
}
