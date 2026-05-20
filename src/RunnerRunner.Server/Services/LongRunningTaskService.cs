using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Hubs;

namespace RunnerRunner.Server.Services;

public sealed class LongRunningTaskService : IDisposable
{
    private const int MaxCompletedTasks = 20;
    private readonly object _gate = new();
    private readonly Dictionary<string, LongRunningTaskInfo> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<LongRunningTaskService> _logger;

    public event Action? OnChanged;

    public LongRunningTaskService(ILogger<LongRunningTaskService> logger)
    {
        _logger = logger;
        StreamSubscriptionService.OnImagePullProgressReceived += HandleImagePullProgress;
        StreamSubscriptionService.OnImagePullCompleteReceived += HandleImagePullComplete;
        AgentHub.OnImagePullProgressReceived += HandleImagePullProgress;
        AgentHub.OnImagePullCompleteReceived += HandleImagePullComplete;
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _tasks.Values.Count(t => t.Status == LongRunningTaskStatus.Running);
        }
    }

    public IReadOnlyList<LongRunningTaskInfo> GetSnapshot()
    {
        lock (_gate)
        {
            return _tasks.Values
                .OrderBy(t => t.Status == LongRunningTaskStatus.Running ? 0 : 1)
                .ThenByDescending(t => t.UpdatedAt)
                .Select(t => t.Clone())
                .ToList();
        }
    }

    public string TrackImagePull(string hostId, PullImageCommand command)
    {
        var taskId = string.IsNullOrWhiteSpace(command.TaskId)
            ? Guid.NewGuid().ToString("N")
            : command.TaskId;

        command.TaskId = taskId;

        var now = DateTimeOffset.UtcNow;
        var imageName = FormatImageName(command);
        lock (_gate)
        {
            _tasks[taskId] = new LongRunningTaskInfo
            {
                Id = taskId,
                Kind = LongRunningTaskKind.ImagePull,
                Title = $"Pull {imageName}",
                Location = hostId,
                HostId = hostId,
                Subject = imageName,
                Status = LongRunningTaskStatus.Running,
                StatusText = "Queued",
                StartedAt = now,
                UpdatedAt = now
            };
        }

        NotifyChanged();
        return taskId;
    }

    public void MarkFailed(string taskId, string error)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return;

        var changed = false;
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                task.Status = LongRunningTaskStatus.Failed;
                task.StatusText = "Failed";
                task.Error = error;
                task.CompletedAt = DateTimeOffset.UtcNow;
                task.UpdatedAt = task.CompletedAt.Value;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    public void ClearCompleted()
    {
        lock (_gate)
        {
            foreach (var id in _tasks.Values
                .Where(t => t.Status != LongRunningTaskStatus.Running)
                .Select(t => t.Id)
                .ToArray())
            {
                _tasks.Remove(id);
            }
        }

        NotifyChanged();
    }

    public void Dispose()
    {
        StreamSubscriptionService.OnImagePullProgressReceived -= HandleImagePullProgress;
        StreamSubscriptionService.OnImagePullCompleteReceived -= HandleImagePullComplete;
        AgentHub.OnImagePullProgressReceived -= HandleImagePullProgress;
        AgentHub.OnImagePullCompleteReceived -= HandleImagePullComplete;
    }

    private void HandleImagePullProgress(ImagePullProgressEvent evt)
    {
        var taskId = ResolveTaskId(evt.TaskId, evt.HostId, evt.ImageType, evt.ImageName);
        if (taskId == null)
        {
            _logger.LogDebug("Ignoring image pull progress for untracked image {Image} on {Host}", evt.ImageName, evt.HostId);
            return;
        }

        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return;

            task.ProgressPercent = Math.Clamp(evt.ProgressPercent, 0, 100);
            task.StatusText = FormatImagePullStatus(evt);
            task.Details = evt.Layers
                .Select((layer, index) => new LongRunningTaskDetailInfo
                {
                    Label = FormatLayerLabel(layer, index),
                    StatusText = string.IsNullOrWhiteSpace(layer.Status) ? "Pulling..." : layer.Status,
                    ProgressPercent = Math.Clamp(layer.ProgressPercent, 0, 100),
                    BytesDownloaded = layer.BytesDownloaded,
                    BytesTotal = layer.BytesTotal,
                    IsComplete = layer.IsComplete
                })
                .ToList();
            task.UpdatedAt = DateTimeOffset.UtcNow;
        }

        NotifyChanged();
    }

    private void HandleImagePullComplete(ImagePullCompleteEvent evt)
    {
        var taskId = ResolveTaskId(evt.TaskId, evt.HostId, evt.ImageType, evt.ImageName);
        if (taskId == null)
        {
            _logger.LogDebug("Ignoring image pull completion for untracked image {Image} on {Host}", evt.ImageName, evt.HostId);
            return;
        }

        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return;

            task.Status = evt.Success ? LongRunningTaskStatus.Succeeded : LongRunningTaskStatus.Failed;
            task.ProgressPercent = evt.Success ? 100 : task.ProgressPercent;
            task.StatusText = evt.Success ? FormatCompleteStatus(task) : "Failed";
            task.Error = evt.Success ? null : evt.Error;
            if (evt.Success && task.Details.Count > 0)
            {
                foreach (var detail in task.Details)
                {
                    detail.ProgressPercent = 100;
                    detail.IsComplete = true;
                }
            }
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.UpdatedAt = task.CompletedAt.Value;
            PruneCompletedTasks();
        }

        NotifyChanged();
    }

    private string? ResolveTaskId(string? taskId, string hostId, ImageType imageType, string imageName)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(taskId) && _tasks.ContainsKey(taskId))
                return taskId;

            return _tasks.Values
                .Where(t =>
                    t.Status == LongRunningTaskStatus.Running &&
                    string.Equals(t.HostId, hostId, StringComparison.OrdinalIgnoreCase) &&
                    t.Kind == LongRunningTaskKind.ImagePull &&
                    string.Equals(t.Subject, imageName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.StartedAt)
                .Select(t => t.Id)
                .FirstOrDefault();
        }
    }

    private void PruneCompletedTasks()
    {
        var completed = _tasks.Values
            .Where(t => t.Status != LongRunningTaskStatus.Running)
            .OrderByDescending(t => t.UpdatedAt)
            .Skip(MaxCompletedTasks)
            .Select(t => t.Id)
            .ToArray();

        foreach (var id in completed)
            _tasks.Remove(id);
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    private static string FormatImageName(PullImageCommand command) =>
        ImageReference.Build(command.RegistryUrl, command.ImageName, command.Tag);

    private static string FormatBytes(long downloaded, long total)
    {
        if (downloaded <= 0 || total <= 0)
            return "Pulling...";

        return $"{FormatSize(downloaded)} / {FormatSize(total)}";
    }

    private static string FormatImagePullStatus(ImagePullProgressEvent evt)
    {
        var status = string.IsNullOrWhiteSpace(evt.Status)
            ? FormatBytes(evt.BytesDownloaded, evt.BytesTotal)
            : evt.Status;

        if (evt.Layers.Count == 0)
            return status;

        var completed = evt.Layers.Count(l => l.IsComplete || l.ProgressPercent >= 100);
        var layerText = $"{completed}/{evt.Layers.Count} layers complete";
        var byteText = evt.BytesDownloaded > 0 && evt.BytesTotal > 0
            ? FormatBytes(evt.BytesDownloaded, evt.BytesTotal)
            : null;

        return string.IsNullOrWhiteSpace(byteText)
            ? $"{layerText} · {status}"
            : $"{layerText} · {byteText} · {status}";
    }

    private static string FormatCompleteStatus(LongRunningTaskInfo task) =>
        task.Details.Count == 0
            ? "Complete"
            : $"{task.Details.Count}/{task.Details.Count} layers complete";

    private static string FormatLayerLabel(ImagePullLayerProgress layer, int index)
    {
        if (string.IsNullOrWhiteSpace(layer.Id))
            return $"Layer {index + 1}";

        return layer.Id.Length > 12 ? layer.Id[..12] : layer.Id;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}

public sealed class LongRunningTaskInfo
{
    public required string Id { get; init; }
    public LongRunningTaskKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Location { get; init; }
    public string? HostId { get; init; }
    public required string Subject { get; init; }
    public LongRunningTaskStatus Status { get; set; }
    public string StatusText { get; set; } = "";
    public double ProgressPercent { get; set; }
    public string? Error { get; set; }
    public List<LongRunningTaskDetailInfo> Details { get; set; } = [];
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public LongRunningTaskInfo Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Location = Location,
        HostId = HostId,
        Subject = Subject,
        Status = Status,
        StatusText = StatusText,
        ProgressPercent = ProgressPercent,
        Error = Error,
        Details = Details.Select(d => d.Clone()).ToList(),
        StartedAt = StartedAt,
        UpdatedAt = UpdatedAt,
        CompletedAt = CompletedAt
    };
}

public sealed class LongRunningTaskDetailInfo
{
    public required string Label { get; init; }
    public string StatusText { get; set; } = "";
    public double ProgressPercent { get; set; }
    public long BytesDownloaded { get; set; }
    public long BytesTotal { get; set; }
    public bool IsComplete { get; set; }

    public LongRunningTaskDetailInfo Clone() => new()
    {
        Label = Label,
        StatusText = StatusText,
        ProgressPercent = ProgressPercent,
        BytesDownloaded = BytesDownloaded,
        BytesTotal = BytesTotal,
        IsComplete = IsComplete
    };
}

public enum LongRunningTaskKind
{
    ImagePull,
    RunnerDeployment,
    RunnerTeardown,
    HostWorkerUpdate,
    AgentDownload,
    InitStep,
    TartVmSetup,
    Cleanup,
    Reconciliation,
    ProviderRegistration
}

public enum LongRunningTaskStatus
{
    Running,
    Succeeded,
    Failed
}
