using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services.Admission;

/// <summary>
/// Durable capacity ownership. All changes for a host take the same PostgreSQL
/// transaction lock. Reservations survive server restarts, lost acknowledgments
/// and Orleans timeouts; only an authenticated native cleanup receipt releases them.
/// </summary>
public sealed class PostgresResourceAdmission(string connectionString) : IResourceAdmission
{
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, "schema", ct);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS host_resource_budgets (
                host_id text PRIMARY KEY,
                cpu_count integer NOT NULL CHECK (cpu_count >= 0),
                memory_mib bigint NOT NULL CHECK (memory_mib >= 0),
                disk_gib bigint NOT NULL CHECK (disk_gib >= 0),
                docker_slots integer NOT NULL CHECK (docker_slots >= 0),
                tart_slots integer NOT NULL CHECK (tart_slots >= 0),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS resource_reservations (
                instance_id text PRIMARY KEY,
                host_id text NOT NULL REFERENCES host_resource_budgets(host_id),
                profile_id text NOT NULL,
                backend integer NOT NULL,
                cpu_count integer NOT NULL CHECK (cpu_count > 0),
                memory_mib bigint NOT NULL CHECK (memory_mib > 0),
                disk_gib bigint NOT NULL CHECK (disk_gib > 0),
                instance_handle text,
                reserved_at timestamptz NOT NULL DEFAULT now(),
                started_at timestamptz,
                released_at timestamptz,
                never_started boolean NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS resource_reservations_active_host
                ON resource_reservations(host_id) WHERE released_at IS NULL;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReserveAsync(string hostId, DeployRunnerCommand deployment, CancellationToken ct = default)
    {
        RequireId(hostId);
        RequireId(deployment.InstanceId);
        var amounts = ResourceAmounts.FromCommand(deployment);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, hostId, ct);
        var budget = await ReadBudgetAsync(connection, transaction, hostId, ct)
            ?? throw new ResourceAdmissionException("Host job budget has not been configured; admission is closed.");
        var usage = await ReadUsageAsync(connection, transaction, hostId, ct);
        if (amounts.CpuCount > budget.CpuCount - usage.Cpu ||
            amounts.MemoryMiB > budget.MemoryMiB - usage.Memory ||
            amounts.DiskGiB > budget.DiskGiB - usage.Disk ||
            (deployment.Backend == ExecutionBackend.Docker && usage.Docker >= budget.DockerSlots) ||
            (deployment.Backend == ExecutionBackend.Tart && usage.Tart >= budget.TartSlots))
            throw new ResourceAdmissionException("Host job budget is fully reserved; uncertain deployments retain their reservations.");

        await using var insert = new NpgsqlCommand("""
            INSERT INTO resource_reservations(instance_id,host_id,profile_id,backend,cpu_count,memory_mib,disk_gib)
            VALUES (@instance,@host,@profile,@backend,@cpu,@memory,@disk)
            ON CONFLICT (instance_id) DO NOTHING;
            """, connection, transaction);
        insert.Parameters.AddWithValue("instance", deployment.InstanceId);
        insert.Parameters.AddWithValue("host", hostId);
        insert.Parameters.AddWithValue("profile", deployment.ProfileId);
        insert.Parameters.AddWithValue("backend", (int)deployment.Backend);
        AddAmounts(insert, amounts.CpuCount, amounts.MemoryMiB, amounts.DiskGiB);
        if (await insert.ExecuteNonQueryAsync(ct) != 1)
            throw new ResourceAdmissionException("Instance ID already has a durable reservation; reconcile it instead of dispatching again.");
        await transaction.CommitAsync(ct);
    }

    public async Task SetBudgetAsync(string hostId, HostResourceBudget budget, CancellationToken ct = default)
    {
        RequireId(hostId);
        budget.Validate();
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, hostId, ct);
        var usage = await ReadUsageAsync(connection, transaction, hostId, ct);
        if (budget.CpuCount < usage.Cpu || budget.MemoryMiB < usage.Memory || budget.DiskGiB < usage.Disk ||
            budget.DockerSlots < usage.Docker || budget.TartSlots < usage.Tart)
            throw new ResourceAdmissionException("A budget cannot be reduced below outstanding reservations. Drain and confirm cleanup first.");
        await using var command = new NpgsqlCommand("""
            INSERT INTO host_resource_budgets(host_id,cpu_count,memory_mib,disk_gib,docker_slots,tart_slots)
            VALUES (@host,@cpu,@memory,@disk,@docker,@tart)
            ON CONFLICT (host_id) DO UPDATE SET cpu_count=EXCLUDED.cpu_count,memory_mib=EXCLUDED.memory_mib,
                disk_gib=EXCLUDED.disk_gib,docker_slots=EXCLUDED.docker_slots,tart_slots=EXCLUDED.tart_slots,updated_at=now();
            """, connection, transaction);
        command.Parameters.AddWithValue("host", hostId);
        AddAmounts(command, budget.CpuCount, budget.MemoryMiB, budget.DiskGiB);
        command.Parameters.AddWithValue("docker", budget.DockerSlots);
        command.Parameters.AddWithValue("tart", budget.TartSlots);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<HostResourceBudget?> GetBudgetAsync(string hostId, CancellationToken ct = default)
    {
        RequireId(hostId);
        await using var connection = await OpenAsync(ct);
        return await ReadBudgetAsync(connection, null, hostId, ct);
    }

    public async Task<ResourceReservation?> GetReservationAsync(string instanceId, CancellationToken ct = default)
    {
        RequireId(instanceId);
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT host_id,backend,cpu_count,memory_mib,disk_gib,instance_handle,released_at IS NOT NULL
            FROM resource_reservations WHERE instance_id=@instance;
            """, connection);
        command.Parameters.AddWithValue("instance", instanceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new ResourceReservation(instanceId, reader.GetString(0),
            (ExecutionBackend)reader.GetInt32(1), new(reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6)) : null;
    }

    public async Task<bool> RecordStartedAsync(string hostId, string instanceId, ExecutionBackend backend, string handle, CancellationToken ct = default)
    {
        RequireId(handle);
        return await UpdateReceiptAsync(hostId, instanceId, backend, handle, false, false, ct);
    }

    public Task<bool> ConfirmCleanupAsync(string hostId, string instanceId, ExecutionBackend backend, string? handle, bool neverStarted, CancellationToken ct = default)
    {
        if (neverStarted ? !string.IsNullOrEmpty(handle) : string.IsNullOrWhiteSpace(handle))
            return Task.FromResult(false);
        return UpdateReceiptAsync(hostId, instanceId, backend, handle, true, neverStarted, ct);
    }

    private async Task<bool> UpdateReceiptAsync(string hostId, string instanceId, ExecutionBackend backend,
        string? handle, bool release, bool neverStarted, CancellationToken ct)
    {
        RequireId(hostId);
        RequireId(instanceId);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, hostId, ct);
        // No timeout, disconnect, job conclusion, snapshot or failed dispatch calls this path.
        // A released ID remains a tombstone and can never start again.
        var sql = release ? """
            UPDATE resource_reservations SET released_at=COALESCE(released_at,now()),
                instance_handle=COALESCE(instance_handle,@handle), never_started=@never_started
            WHERE host_id=@host AND instance_id=@instance AND backend=@backend
                AND (instance_handle IS NULL OR instance_handle=@handle)
                AND (NOT @never_started OR (started_at IS NULL AND instance_handle IS NULL))
                AND (released_at IS NULL OR never_started=@never_started);
            """ : """
            UPDATE resource_reservations SET started_at=COALESCE(started_at,now()),instance_handle=@handle
            WHERE host_id=@host AND instance_id=@instance AND backend=@backend AND released_at IS NULL
                AND (instance_handle IS NULL OR instance_handle=@handle);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("host", hostId);
        command.Parameters.AddWithValue("instance", instanceId);
        command.Parameters.AddWithValue("backend", (int)backend);
        command.Parameters.Add("handle", NpgsqlDbType.Text).Value = (object?)handle ?? DBNull.Value;
        command.Parameters.AddWithValue("never_started", neverStarted);
        var accepted = await command.ExecuteNonQueryAsync(ct) == 1;
        await transaction.CommitAsync(ct);
        return accepted;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string hostId, CancellationToken ct)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("RunnerRunner/resource-admission/v1/" + hostId));
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@lock);", connection, transaction);
        command.Parameters.AddWithValue("lock", BinaryPrimitives.ReadInt64BigEndian(bytes));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HostResourceBudget?> ReadBudgetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string hostId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT cpu_count,memory_mib,disk_gib,docker_slots,tart_slots FROM host_resource_budgets WHERE host_id=@host;", connection, transaction);
        command.Parameters.AddWithValue("host", hostId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4)) : null;
    }

    private static async Task<(long Cpu, long Memory, long Disk, long Docker, long Tart)> ReadUsageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string hostId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(SUM(cpu_count),0)::bigint,COALESCE(SUM(memory_mib),0)::bigint,COALESCE(SUM(disk_gib),0)::bigint,
                COUNT(*) FILTER (WHERE backend=@docker),COUNT(*) FILTER (WHERE backend=@tart)
            FROM resource_reservations WHERE host_id=@host AND released_at IS NULL;
            """, connection, transaction);
        command.Parameters.AddWithValue("host", hostId);
        command.Parameters.AddWithValue("docker", (int)ExecutionBackend.Docker);
        command.Parameters.AddWithValue("tart", (int)ExecutionBackend.Tart);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static void AddAmounts(NpgsqlCommand command, int cpu, long memory, long disk)
    {
        command.Parameters.AddWithValue("cpu", cpu);
        command.Parameters.AddWithValue("memory", memory);
        command.Parameters.AddWithValue("disk", disk);
    }

    private static void RequireId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new ArgumentException("A nonempty identity of at most 512 characters is required.");
    }
}
