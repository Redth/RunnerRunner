namespace RunnerRunner.Core.Models;

/// <summary>
/// Audit log entry for a received webhook event and its provisioning outcome.
/// </summary>
public class WebhookEvent
{
    private static readonly HashSet<string> TerminalStatuses =
    [
        "completed",
        "timed_out",
        "rejected",
        "ignored",
        "in_progress"
    ];

    private static readonly HashSet<string> OpenEndedQueueStatuses =
    [
        "pending",
        "pending_fifo",
        "pending_capacity",
        "pending_host_match",
        "pending_config",
        "no_match"
    ];

    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Provisioning rule ID (and legacy binding ID for older records).</summary>
    public string BindingId { get; set; } = "";
    public string Provider { get; set; } = "";

    /// <summary>Webhook action: queued, in_progress, completed, waiting</summary>
    public string Action { get; set; } = "";

    public string JobId { get; set; } = "";
    public string RunId { get; set; } = "";

    /// <summary>Full repository name, e.g. "dotnet/maui"</summary>
    public string Repository { get; set; } = "";
    public string? GitHubInstallationId { get; set; }
    public string WorkflowName { get; set; } = "";

    /// <summary>Labels from the job's runs-on field</summary>
    public List<string> Labels { get; set; } = [];

    public string? MatchedProfileId { get; set; }
    public string? MatchedProfileName { get; set; }
    public string? MatchedRunnerDefinitionId { get; set; }
    public string? MatchedRunnerDefinitionName { get; set; }
    public string? RequestedRunnerTargetKey { get; set; }
    public List<string> ValidRunnerTargetKeys { get; set; } = [];
    public string? RunnerTargetSelectionReason { get; set; }

    /// <summary>RunnerInstance ID created for this event (if provisioned)</summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Image tag override supplied by the webhook via a magic
    /// <c>rr-image-tag=&lt;value&gt;</c> label on the job's <c>runs-on</c>.
    /// Populated whenever the label is seen, even if the matched profile
    /// didn't opt in — so operators can debug. The override is only applied
    /// at dispatch time when the profile has
    /// <see cref="RunnerProfile.AllowWebhookImageTagOverride"/> enabled.
    /// </summary>
    public string? ImageTagOverride { get; set; }

    /// <summary>
    /// Human-readable reason the supplied tag override was not applied
    /// (e.g. "profile did not opt in" or "invalid tag format"). Null when
    /// the override was either absent or successfully applied.
    /// </summary>
    public string? ImageTagOverrideRejectedReason { get; set; }

    /// <summary>
    /// pending*, matching/preparing/dispatching, no_match, rejected, provisioned, in_progress, completed, timed_out, ignored
    /// </summary>
    public string Status { get; set; } = "";
    public string? Error { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextRetryAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int RetryCount { get; set; }

    public bool IsTerminal => TerminalStatuses.Contains(Status);

    public bool IsOpenEndedQueueWait =>
        Action == "queued" && OpenEndedQueueStatuses.Contains(Status);

    public bool IsRetryCandidate(DateTime nowUtc) =>
        Action == "queued"
        && !IsTerminal
        && Status is not "provisioned"
        && (!NextRetryAt.HasValue || NextRetryAt.Value <= nowUtc);

    public bool HasExpired(DateTime nowUtc) =>
        Action == "queued"
        && ExpiresAt.HasValue
        && ExpiresAt.Value <= nowUtc
        && !IsTerminal
        && !IsOpenEndedQueueWait;

    public void EnsureLifecycleWindow(DateTime nowUtc, TimeSpan timeout)
    {
        if (UpdatedAt == default)
            UpdatedAt = ReceivedAt == default ? nowUtc : ReceivedAt;

        NextRetryAt ??= nowUtc;
        if (IsOpenEndedQueueWait)
        {
            ExpiresAt = null;
            return;
        }

        ExpiresAt ??= ReceivedAt == default ? nowUtc.Add(timeout) : ReceivedAt.Add(timeout);
    }

    public void ScheduleRetry(
        string reason,
        DateTime nowUtc,
        TimeSpan delay,
        string status = "pending",
        bool countAttempt = true)
    {
        Status = status;
        Error = reason;
        UpdatedAt = nowUtc;
        LastAttemptAt = nowUtc;
        NextRetryAt = nowUtc.Add(delay);

        if (countAttempt)
            RetryCount++;

        if (OpenEndedQueueStatuses.Contains(status))
            ExpiresAt = null;
    }

    public void MarkResolved(string status, DateTime nowUtc, string? instanceId = null)
    {
        Status = status;
        Error = null;
        UpdatedAt = nowUtc;
        if (!string.IsNullOrWhiteSpace(instanceId))
            InstanceId = instanceId;

        if (status is "completed" or "timed_out")
            ResolvedAt = nowUtc;

        ExpiresAt = null;
        NextRetryAt = null;
    }

    public void SetProgress(string status, string? detail, DateTime nowUtc, DateTime? nextRetryAt = null)
    {
        Status = status;
        Error = detail;
        UpdatedAt = nowUtc;
        LastAttemptAt = nowUtc;
        NextRetryAt = nextRetryAt;
        ResolvedAt = null;
    }
}
