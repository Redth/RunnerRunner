using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.Admission;

// Run against an isolated PostgreSQL test database using a protected configuration file.
// A fresh schema is mandatory; test records are retained for independent audit.
if (args.Length != 4 || args[0] is not ("phase1" or "phase2") ||
    !Regex.IsMatch(args[2], "^rr_admission_probe_[a-z0-9_]{1,40}$"))
    throw new ArgumentException("Usage: phase1|phase2 PROTECTED_CONFIG UNIQUE_SCHEMA PUBLIC_RESULT_DIRECTORY");
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var ct = timeout.Token;
    using var config = JsonDocument.Parse(File.ReadAllText(args[1]));
    var builder = new NpgsqlConnectionStringBuilder(config.RootElement.GetProperty("Database").GetProperty("ConnectionString").GetString());
    var schema = args[2];
    await using (var setup = new NpgsqlConnection(builder.ConnectionString))
    {
        await setup.OpenAsync(ct);
        if (args[0] == "phase1")
        {
            // Identifier is restricted above; no IF NOT EXISTS or stale test reuse.
            await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema};", setup);
            await create.ExecuteNonQueryAsync(ct);
        }
    }
    builder.SearchPath = schema;
    var connection = builder.ConnectionString;
    var admission = new PostgresResourceAdmission(connection);
    var results = Path.GetFullPath(args[3]);
    if (!Directory.Exists(results)) throw new InvalidOperationException("Owned results directory missing");
    if (args[0] == "phase1")
    {
        await admission.EnsureCreatedAsync(ct);
        await Denied(() => admission.ReserveAsync("unconfigured", Command("missing"), ct));
        await admission.SetBudgetAsync("shared", new(4, 8192, 128, 2, 2), ct);
        var winners = await Race(connection, "shared", "shared", 32, ct);
        Require(winners.Count == 2, "shared CPU/memory/disk budget overcommitted or underfilled");
        // Isolate each limiting dimension, using distinct profiles and store/connection instances.
        var dimensions = new Dictionary<string, HostResourceBudget>
        {
            ["cpu"] = new(3, 65536, 1024, 20, 20),
            ["memory"] = new(32, 5000, 1024, 20, 20),
            ["disk"] = new(32, 65536, 90, 20, 20),
            ["slots"] = new(32, 65536, 1024, 1, 0)
        };
        foreach (var (dimension, budget) in dimensions)
        {
            await admission.SetBudgetAsync(dimension, budget, ct);
            Require((await Race(connection, dimension, dimension, 16, ct, dockerOnly: true)).Count == 1,
                $"{dimension} admission failed");
        }
        Save("phase1", new { passed = true, schema, process_id = Environment.ProcessId, attempted = 96,
            shared_winners = winners.Order().ToArray(), each_independent_dimension_winners = 1,
            no_job_backend_or_credentials_used = true });
    }
    else
    {
        using var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(results, "phase1.json")));
        Require(before.RootElement.GetProperty("process_id").GetInt32() != Environment.ProcessId, "Probe must restart as a new process");
        var winners = before.RootElement.GetProperty("shared_winners").EnumerateArray().Select(x => x.GetString()!).ToArray();
        foreach (var id in winners)
            Require(await admission.GetReservationAsync(id, ct) is { Released: false }, "Reservation did not survive process restart");
        await Denied(() => admission.ReserveAsync("shared", Command("after-restart-overflow"), ct));
        await Denied(() => admission.SetBudgetAsync("shared", new(0, 0, 0, 0, 0), ct));
        var first = (await admission.GetReservationAsync(winners[0], ct))!;
        Require(!await admission.RecordStartedAsync("foreign", first.InstanceId, first.Backend, "native-1", ct), "Foreign host claimed start");
        Require(await admission.RecordStartedAsync("shared", first.InstanceId, first.Backend, "native-1", ct), "Owned start rejected");
        Require(!await admission.ConfirmCleanupAsync("foreign", first.InstanceId, first.Backend, "native-1", false, ct), "Foreign host released capacity");
        var otherBackend = first.Backend == ExecutionBackend.Docker ? ExecutionBackend.Tart : ExecutionBackend.Docker;
        Require(!await admission.ConfirmCleanupAsync("shared", first.InstanceId, otherBackend, "native-1", false, ct), "Wrong backend released capacity");
        Require(!await admission.ConfirmCleanupAsync("shared", first.InstanceId, first.Backend, "foreign-handle", false, ct), "Wrong handle released capacity");
        Require(!await admission.ConfirmCleanupAsync("shared", first.InstanceId, first.Backend, null, true, ct), "Started instance reported never started");
        await Denied(() => admission.ReserveAsync("shared", Command("still-overflow"), ct));
        Require(await admission.ConfirmCleanupAsync("shared", first.InstanceId, first.Backend, "native-1", false, ct), "Owned cleanup rejected");
        Require(await admission.ConfirmCleanupAsync("shared", first.InstanceId, first.Backend, "native-1", false, ct), "Repeated cleanup not idempotent");
        Require(!await admission.RecordStartedAsync("shared", first.InstanceId, first.Backend, "native-1", ct), "Late start reopened released reservation");
        await admission.SetBudgetAsync("second-host", new(8, 32768, 512, 4, 4), ct);
        await Denied(() => admission.ReserveAsync("second-host", Command(first.InstanceId), ct));
        await Denied(() => admission.ReserveAsync("shared", Command(first.InstanceId), ct));
        await admission.ReserveAsync("shared", Command("replacement"), ct);
        await Denied(() => admission.ReserveAsync("shared", Command("replacement-overflow"), ct));
        Require(await admission.ConfirmCleanupAsync("shared", "replacement", ExecutionBackend.Docker, null, true, ct), "Never-started receipt rejected");
        Require(!await admission.RecordStartedAsync("shared", "replacement", ExecutionBackend.Docker, "late", ct), "Rejected native creation reopened claim");
        await Denied(() => admission.ReserveAsync("shared", Command("replacement"), ct));
        await admission.ReserveAsync("shared", Command("fresh-replacement"), ct);
        Save("phase2", new { passed = true, schema, process_id = Environment.ProcessId,
            process_restart_retained_capacity = true, foreign_host_backend_handle_rejected = true,
            budget_reduction_below_usage_rejected = true, duplicate_instance_rejected_across_hosts = true,
            cleanup_idempotent = true, late_start_cannot_reopen = true, fresh_replacement_admitted = true,
            never_started_receipt_qualified = true, no_job_backend_or_credentials_used = true });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { phase = args[0], passed = true, schema }));

    void Save(string phase, object result)
    {
        using var stream = new FileStream(Path.Combine(results, phase + ".json"), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(true);
    }
}
catch (Exception ex)
{
    // Connection/config exceptions must never dump protected connection strings.
    Console.Error.WriteLine(JsonSerializer.Serialize(new { phase = args[0], passed = false, error_type = ex.GetType().Name }));
    Environment.ExitCode = 1;
}

static DeployRunnerCommand Command(string id, bool tart = false) => new()
{
    InstanceId = id, ProfileId = "profile-" + id, RunnerName = "probe-" + id,
    Backend = tart ? ExecutionBackend.Tart : ExecutionBackend.Docker,
    DockerConfig = new() { RegistryUrl = "", ImageName = "not-executed", CpuCount = 2, MemorySizeMb = 4096, DiskSizeGb = 64 },
    TartConfig = new() { RegistryUrl = "", ImageName = "not-executed", CpuCount = 2, MemorySizeGb = 4, DiskSizeGb = 64 }
};

static async Task<List<string>> Race(string connection, string host, string prefix, int count, CancellationToken ct, bool dockerOnly = false)
{
    var winners = new ConcurrentBag<string>();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = Enumerable.Range(0, count).Select(async i =>
    {
        await gate.Task;
        var id = $"{prefix}-{i}";
        try
        {
            await new PostgresResourceAdmission(connection).ReserveAsync(host, Command(id, !dockerOnly && i % 2 == 1), ct);
            winners.Add(id);
        }
        catch (ResourceAdmissionException) { }
    }).ToArray();
    gate.SetResult();
    await Task.WhenAll(tasks);
    return winners.ToList();
}

static async Task Denied(Func<Task> action)
{
    try { await action(); }
    catch (ResourceAdmissionException) { return; }
    throw new InvalidOperationException("Admission unexpectedly succeeded");
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
