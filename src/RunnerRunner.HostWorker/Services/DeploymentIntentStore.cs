using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.HostWorker.Services;

/// <summary>
/// A durable, single-use claim on an instance ID, written before any backend
/// side effect. A disconnected/restarted worker must reconcile the existing
/// environment instead of replaying its deployment or JIT credentials.
/// </summary>
internal sealed class DeploymentIntentStore
{
    private readonly string _root;
    private readonly string _hostId;
    private readonly object _gate = new();
    private string? _receiptCursor;
    private string? _recoveryCursor;

    public DeploymentIntentStore(HostWorkerPaths paths, string hostId)
    {
        _root = Path.Combine(paths.DataRoot, "deployment-intents");
        _hostId = hostId;
        Directory.CreateDirectory(_root);
    }

    public bool Contains(string instanceId) => File.Exists(PathFor(instanceId));

    public DeploymentIntent? Read(string instanceId)
    {
        lock (_gate)
        {
            if (!Contains(instanceId)) return null;
            var intent = ReadPath(PathFor(instanceId));
            if (intent.InstanceId != instanceId) throw new InvalidDataException("Deployment intent ownership differs.");
            return intent;
        }
    }

    public DeploymentIntent Begin(DeployRunnerCommand command)
    {
        var intent = new DeploymentIntent
        {
            HostId = _hostId,
            InstanceId = command.InstanceId,
            ProfileId = command.ProfileId,
            RunnerName = command.RunnerName,
            Backend = command.Backend,
            CreatedAt = DateTime.UtcNow,
            DeferredRegistration = command.DeferRegistration,
            IsEphemeral = command.Ephemeral || command.DeferRegistration || !string.IsNullOrEmpty(command.JitConfig),
            Ownership = command.Backend == ExecutionBackend.Docker ? new NativeRunnerOwnership
            {
                HostId = _hostId, InstanceId = command.InstanceId, IntentId = Guid.NewGuid().ToString("N"),
                ProfileId = command.ProfileId, RunnerName = command.RunnerName,
                CpuCount = command.DockerConfig?.CpuCount, MemoryMiB = command.DockerConfig?.MemorySizeMb,
                DiskGiB = command.DockerConfig?.DiskSizeGb, HyperVIsolation = command.DockerConfig?.HyperVIsolation ?? false
            } : null
        };
        // CreateNew arbitrates concurrent deliveries and even separate processes.
        // A torn/empty claim also blocks replay: it is uncertain, never "not run".
        WriteNew(PathFor(command.InstanceId), intent);
        return intent;
    }

    public void MarkCreating(string instanceId, NativeRunnerOwnership ownership)
    {
        lock (_gate)
        {
            var intent = Read(instanceId) ?? throw new InvalidDataException("Creation has no ownership claim.");
            if (intent.State != "claimed" || intent.Ownership is null || string.IsNullOrWhiteSpace(ownership.ImageId)
                || ownership.Platform is not ("windows" or "linux")
                || intent.Ownership != (ownership with { ImageId = null, Platform = null }))
                throw new InvalidDataException("Native creation is not bound to the original claim.");
            intent.Ownership = ownership;
            intent.State = "creating";
            Save(intent);
        }
    }

    public void MarkCreated(string instanceId, string handle) => Update(instanceId, "created", handle);

    public IReadOnlyList<DeploymentIntent> RecoveryCandidates(int limit = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            var pending = Directory.EnumerateFiles(_root, "*.json").Select(ReadPath)
                .Where(i => i.State != "released" && i.Ownership is not null)
                .OrderBy(i => i.InstanceId, StringComparer.Ordinal).ToArray();
            var batch = pending.Where(i => _recoveryCursor is null || StringComparer.Ordinal.Compare(i.InstanceId, _recoveryCursor) > 0)
                .Concat(pending.Where(i => _recoveryCursor is not null && StringComparer.Ordinal.Compare(i.InstanceId, _recoveryCursor) <= 0))
                .Take(limit).ToArray();
            if (batch.Length > 0) _recoveryCursor = batch[^1].InstanceId;
            return batch;
        }
    }

    public void MarkPrepared(string instanceId, string handle) => Update(instanceId, "prepared", handle);

    public void ClaimActivation(string instanceId, string handle)
    {
        lock (_gate)
        {
            var intent = Read(instanceId) ?? throw new InvalidDataException("Activation has no durable ownership.");
            if (!intent.DeferredRegistration || intent.State != "prepared" || intent.InstanceHandle != handle)
                throw new InvalidDataException("Activation is not bound to an unconsumed prepared environment.");
            intent.State = "activating";
            Save(intent);
        }
    }

    public void MarkStarted(string instanceId, string handle)
        => Update(instanceId, "started", handle);

    public RunnerStoppedEvent CompleteCleanup(string instanceId, string? handle, bool neverStarted, string reason)
    {
        if (reason is not ("completed" or "stopped" or "crashed" or "failed"))
            throw new ArgumentException("Cleanup reason must be a public outcome code.", nameof(reason));
        lock (_gate)
        {
            var intent = Read(instanceId) ?? throw new InvalidDataException("Cleanup has no durable ownership.");

            if (intent.CleanupReceipt is { } existing)
            {
                if (existing.NeverStarted != neverStarted || existing.InstanceHandle != handle)
                    throw new InvalidDataException("Cleanup receipt cannot change its ownership proof.");
                return existing;
            }
            if (neverStarted ? handle is not null || intent.InstanceHandle is not null || intent.State != "claimed"
                : string.IsNullOrWhiteSpace(handle) || (intent.InstanceHandle is not null && intent.InstanceHandle != handle))
                throw new InvalidDataException("Cleanup does not match durable native ownership.");

            // Old released records without a receipt cannot safely be upgraded by guessing.
            if (intent.State == "released") throw new InvalidDataException("Legacy release has no replayable proof.");
            intent.State = "released";
            intent.InstanceHandle = handle;
            intent.CleanupReceipt = new RunnerStoppedEvent
            {
                InstanceId = instanceId, Backend = intent.Backend, InstanceHandle = handle,
                NeverStarted = neverStarted, CleanupConfirmed = true, Reason = reason,
                CleanupReceiptId = Guid.NewGuid().ToString("N"),
                // Native errors may contain secrets. Persist only fixed public outcomes.
                ErrorMessage = reason == "crashed" ? "Owned runner exited unsuccessfully; native cleanup confirmed."
                    : reason == "failed" ? "Backend not available or host capacity is full; native creation was not attempted." : null
            };
            Save(intent);
            return intent.CleanupReceipt;
        }
    }

    public void AcknowledgeCleanup(RunnerStoppedEvent acknowledgement)
    {
        lock (_gate)
        {
            var intent = Read(acknowledgement.InstanceId) ?? throw new InvalidDataException("Unknown cleanup acknowledgement.");
            var receipt = intent.CleanupReceipt;
            if (receipt is null || string.IsNullOrWhiteSpace(acknowledgement.CleanupReceiptId)
                || receipt.CleanupReceiptId != acknowledgement.CleanupReceiptId || !acknowledgement.CleanupConfirmed
                || receipt.Backend != acknowledgement.Backend || receipt.InstanceHandle != acknowledgement.InstanceHandle
                || receipt.NeverStarted != acknowledgement.NeverStarted)
                throw new InvalidDataException("Cleanup acknowledgement differs from durable receipt.");
            if (intent.CleanupAcknowledgedAt is not null) return;
            intent.CleanupAcknowledgedAt = DateTime.UtcNow;
            Save(intent);
        }
    }

    public IReadOnlyList<RunnerStoppedEvent> PendingCleanupReceipts(int limit = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            // Rotate batches so an unavailable/old server cannot starve later receipts.
            var pending = Directory.EnumerateFiles(_root, "*.json")
                .Select(ReadPath).Where(i => i.CleanupReceipt is not null && i.CleanupAcknowledgedAt is null)
                .OrderBy(i => i.InstanceId, StringComparer.Ordinal).ToArray();
            var batch = pending.Where(i => _receiptCursor is null || StringComparer.Ordinal.Compare(i.InstanceId, _receiptCursor) > 0)
                .Concat(pending.Where(i => _receiptCursor is not null && StringComparer.Ordinal.Compare(i.InstanceId, _receiptCursor) <= 0))
                .Take(limit).Select(i => i.CleanupReceipt!).ToArray();
            if (batch.Length > 0) _receiptCursor = batch[^1].InstanceId;
            return batch;
        }
    }

    private void Update(string instanceId, string state, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) throw new ArgumentException("Native handle is required.", nameof(handle));
        lock (_gate)
        {
            var intent = Read(instanceId) ?? throw new InvalidDataException("Deployment intent is missing.");
            // An immediately exiting container can finish before Start returns.
            // A delayed startup receipt must never reopen that closed claim.
            if (intent.State == "released")
                return;

            if (intent.InstanceHandle is not null && intent.InstanceHandle != handle)
                throw new InvalidDataException("Native handle cannot change within an ownership claim.");

            intent.State = state;
            intent.InstanceHandle = handle ?? intent.InstanceHandle;
            Save(intent);
        }
    }

    private DeploymentIntent ReadPath(string path)
    {
        var intent = JsonSerializer.Deserialize<DeploymentIntent>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Deployment intent is unreadable; reconciliation required.");
        if (intent.SchemaVersion is < 1 or > 2 || intent.HostId != _hostId || PathFor(intent.InstanceId) != path)
            throw new InvalidDataException("Deployment intent ownership or schema differs.");
        if (intent.Ownership is { } owner && (owner.HostId != intent.HostId || owner.InstanceId != intent.InstanceId
            || owner.ProfileId != intent.ProfileId || owner.RunnerName != intent.RunnerName
            || !Guid.TryParseExact(owner.IntentId, "N", out _) || intent.Backend != ExecutionBackend.Docker))
            throw new InvalidDataException("Native ownership differs from durable deployment.");
        if (intent.CleanupReceipt is { } receipt && (intent.State != "released" || !receipt.CleanupConfirmed
            || receipt.InstanceId != intent.InstanceId || receipt.Backend != intent.Backend
            || receipt.InstanceHandle != intent.InstanceHandle || string.IsNullOrWhiteSpace(receipt.CleanupReceiptId)))
            throw new InvalidDataException("Cleanup receipt ownership differs.");
        return intent;
    }

    private void Save(DeploymentIntent intent)
    {
        var path = PathFor(intent.InstanceId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteNew(temp, intent);
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))) + ".json");
    }

    private static void WriteNew(string path, DeploymentIntent intent)
    {
        // Explicit fields exclude JIT blobs, environment variables, registry
        // passwords and registration tokens from this durable ownership record.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal sealed class DeploymentIntent
    {
        public int SchemaVersion { get; set; } = 2;
        public string HostId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string RunnerName { get; set; } = "";
        public ExecutionBackend Backend { get; set; }
        public DateTime CreatedAt { get; set; }
        public string State { get; set; } = "claimed";
        public string? InstanceHandle { get; set; }
        public bool IsEphemeral { get; set; }
        public bool DeferredRegistration { get; set; }
        public NativeRunnerOwnership? Ownership { get; set; }
        public RunnerStoppedEvent? CleanupReceipt { get; set; }
        public DateTime? CleanupAcknowledgedAt { get; set; }
    }
}
