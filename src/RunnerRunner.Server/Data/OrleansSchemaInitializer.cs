using Npgsql;

namespace RunnerRunner.Server.Data;

public static class OrleansSchemaInitializer
{
    private const long AdvisoryLockId = 0x52756E6E657201L;

    private static readonly SchemaScript[] Scripts =
    [
        new("orleansquery", "00-PostgreSQL-Main.sql"),
        new("orleansmembershiptable", "01-PostgreSQL-Clustering.sql"),
        new("orleansstorage", "02-PostgreSQL-Persistence.sql"),
        new("orleansreminderstable", "03-PostgreSQL-Reminders.sql"),
    ];

    public static async Task EnsureCreatedAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var scriptDirectory = Path.Combine(AppContext.BaseDirectory, "postgres-init");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            $"SELECT pg_advisory_lock({AdvisoryLockId});",
            cancellationToken);

        try
        {
            foreach (var script in Scripts)
            {
                if (await TableExistsAsync(connection, script.RequiredTable, cancellationToken))
                {
                    continue;
                }

                var scriptPath = Path.Combine(scriptDirectory, script.FileName);
                if (!File.Exists(scriptPath))
                {
                    throw new FileNotFoundException(
                        $"Required Orleans PostgreSQL schema script was not found: {scriptPath}",
                        scriptPath);
                }

                logger.LogInformation(
                    "Applying Orleans PostgreSQL schema script {ScriptFile}.",
                    script.FileName);

                var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);
                await ExecuteNonQueryAsync(connection, sql, cancellationToken);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connection,
                $"SELECT pg_advisory_unlock({AdvisoryLockId});",
                CancellationToken.None);
        }
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table_name) IS NOT NULL;";
        command.Parameters.AddWithValue("table_name", $"public.{tableName}");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record SchemaScript(string RequiredTable, string FileName);
}
