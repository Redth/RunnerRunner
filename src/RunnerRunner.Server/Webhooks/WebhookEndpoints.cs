using System.Text;
using System.Text.Json;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Webhooks;

public static class WebhookEndpoints
{
    /// <summary>
    /// Fired when a "queued" workflow_job event is matched to a profile.
    /// Parameters: (WebhookEvent, profileId)
    /// </summary>
    public static event Action<WebhookEvent, string>? OnJobQueued;
    public static event Action<string, string>? OnJobCompleted; // jobId, conclusion

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

        // Read raw bytes for exact HMAC validation before decoding JSON.
        ctx.Request.EnableBuffering();
        using var bodyBuffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyBuffer);
        var bodyBytes = bodyBuffer.ToArray();
        var body = Encoding.UTF8.GetString(bodyBytes);
        ctx.Request.Body.Position = 0;

        // Parse event type header
        var eventType = provider == RunnerProvider.GitHubActions
            ? ctx.Request.Headers["X-GitHub-Event"].FirstOrDefault()
            : ctx.Request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "workflow_job";

        if (eventType != "workflow_job")
            return Results.Ok(new { message = $"Ignored event type: {eventType}" });

        var signature = provider == RunnerProvider.GitHubActions
            ? ctx.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
            : ctx.Request.Headers["X-Gitea-Signature"].FirstOrDefault();

        // Delegate to the WebhookProcessorGrain for matching and auditing.
        // DynamicProvisioningService receives the event below and owns webhook runner dispatch during the grain migration.
        var providerString = provider == RunnerProvider.GitHubActions ? "github" : "gitea";
        var grainFactory = ctx.RequestServices.GetRequiredService<IGrainFactory>();
        var processorGrain = grainFactory.GetGrain<IWebhookProcessorGrain>(0);
        var result = await processorGrain.ProcessWebhook(providerString, body, bodyBytes, signature);

        logger.LogInformation("Webhook processed: {Status} - {Message}", result.Status, result.Message);

        // Fire events for DynamicProvisioningService integration
        if (result.Status == "provisioned" && result.ProfileId != null && result.EventId != null)
        {
            try
            {
                using var scope = ctx.RequestServices.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
                var webhookEvent = await store.Get<WebhookEvent>(result.EventId);
                if (webhookEvent != null)
                {
                    OnJobQueued?.Invoke(webhookEvent, result.ProfileId);
                }
                else
                {
                    logger.LogWarning("Grain returned EventId {EventId} but event not found in store", result.EventId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fire OnJobQueued event");
            }
        }
        else if (result.Status == "completed")
        {
            try
            {
                var json = JsonDocument.Parse(body).RootElement;
                var jobId = json.GetProperty("workflow_job").GetProperty("id").GetInt64().ToString();
                var conclusion = "";
                if (json.TryGetProperty("workflow_job", out var wfJob2) &&
                    wfJob2.TryGetProperty("conclusion", out var conclusionProp))
                    conclusion = conclusionProp.GetString() ?? "";
                OnJobCompleted?.Invoke(jobId, conclusion);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fire OnJobCompleted event");
            }
        }

        if (result.Status == "rejected")
            return Results.Unauthorized();

        return Results.Ok(new { message = result.Message, status = result.Status, profileId = result.ProfileId });
    }
}
