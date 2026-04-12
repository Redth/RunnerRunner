namespace RunnerRunner.Core.Models;

/// <summary>
/// Audit log entry for a received webhook event.
/// </summary>
public class WebhookEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BindingId { get; set; } = "";
    public string Provider { get; set; } = "";

    /// <summary>Webhook action: queued, in_progress, completed, waiting</summary>
    public string Action { get; set; } = "";

    public string JobId { get; set; } = "";
    public string RunId { get; set; } = "";

    /// <summary>Full repository name, e.g. "dotnet/maui"</summary>
    public string Repository { get; set; } = "";
    public string WorkflowName { get; set; } = "";

    /// <summary>Labels from the job's runs-on field</summary>
    public List<string> Labels { get; set; } = [];

    public string? MatchedProfileId { get; set; }
    public string? MatchedProfileName { get; set; }

    /// <summary>RunnerInstance ID created for this event (if provisioned)</summary>
    public string? InstanceId { get; set; }

    /// <summary>matched, no_match, rejected, provisioned, rate_limited, error</summary>
    public string Status { get; set; } = "";
    public string? Error { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
