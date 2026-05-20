using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using RunnerRunner.Server.Authentication;
using System.Data.Common;

namespace RunnerRunner.Server.Data.Auth;

public static class RunnerRunnerAuthSchemaInitializer
{
    private const long AdvisoryLockId = 0x52756E6E41757468L;
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    public static async Task EnsureCreatedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerRunnerAuthDbContext>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        var lockAcquired = false;

        try
        {
            if (IsNpgsql(db))
            {
                await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({AdvisoryLockId});", cancellationToken);
                lockAcquired = true;
            }

            await EnsureSchemaCreatedAsync(db, cancellationToken);

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var role in RunnerRunnerRoles.All)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    var result = await roleManager.CreateAsync(new IdentityRole(role));
                    if (!result.Succeeded)
                        throw new InvalidOperationException($"Unable to create role '{role}': {string.Join("; ", result.Errors.Select(x => x.Description))}");
                }
            }
        }
        finally
        {
            try
            {
                if (lockAcquired)
                {
                    await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({AdvisoryLockId});");
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static async Task EnsureSchemaCreatedAsync(
        RunnerRunnerAuthDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.Database.EnsureCreatedAsync(cancellationToken))
            return;

        var authTables = GetAuthTables(db);
        var existingTables = await GetExistingAuthTablesAsync(db, authTables, cancellationToken);

        if (existingTables.Count == authTables.Count)
            return;

        if (existingTables.Count > 0)
        {
            var missingTables = authTables
                .Where(table => !existingTables.Contains(table.Name))
                .Select(table => table.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            throw new InvalidOperationException(
                $"The RunnerRunner auth schema is incomplete. Missing tables: {string.Join(", ", missingTables)}.");
        }

        var databaseCreator = db.GetService<IRelationalDatabaseCreator>();
        await databaseCreator.CreateTablesAsync(cancellationToken);
    }

    private static IReadOnlyList<AuthTable> GetAuthTables(RunnerRunnerAuthDbContext db) =>
        db.Model.GetEntityTypes()
            .Select(entityType => new AuthTable(entityType.GetSchema() ?? "public", entityType.GetTableName() ?? ""))
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .Distinct()
            .ToArray();

    private static async Task<HashSet<string>> GetExistingAuthTablesAsync(
        RunnerRunnerAuthDbContext db,
        IReadOnlyList<AuthTable> authTables,
        CancellationToken cancellationToken)
    {
        if (authTables.Count == 0)
            return [];

        if (IsNpgsql(db))
            return await GetPostgresTablesAsync(db, authTables, cancellationToken);

        if (string.Equals(db.Database.ProviderName, SqliteProviderName, StringComparison.Ordinal))
            return await GetSqliteTablesAsync(db, authTables, cancellationToken);

        throw new NotSupportedException(
            $"RunnerRunner auth schema initialization does not support provider '{db.Database.ProviderName}'.");
    }

    private static async Task<HashSet<string>> GetPostgresTablesAsync(
        RunnerRunnerAuthDbContext db,
        IReadOnlyList<AuthTable> authTables,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();

        var predicates = new List<string>(authTables.Count);
        for (var i = 0; i < authTables.Count; i++)
        {
            var schemaParameterName = $"@schema{i}";
            var nameParameterName = $"@name{i}";
            predicates.Add($"(table_schema = {schemaParameterName} AND table_name = {nameParameterName})");
            AddParameter(command, schemaParameterName, authTables[i].Schema);
            AddParameter(command, nameParameterName, authTables[i].Name);
        }

        command.CommandText = $"""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
              AND ({string.Join(" OR ", predicates)});
            """;

        return await ExecuteTableNamesAsync(command, cancellationToken);
    }

    private static async Task<HashSet<string>> GetSqliteTablesAsync(
        RunnerRunnerAuthDbContext db,
        IReadOnlyList<AuthTable> authTables,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();

        var parameters = new List<string>(authTables.Count);
        for (var i = 0; i < authTables.Count; i++)
        {
            var parameterName = $"@name{i}";
            parameters.Add(parameterName);
            AddParameter(command, parameterName, authTables[i].Name);
        }

        command.CommandText = $"""
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ({string.Join(", ", parameters)});
            """;

        return await ExecuteTableNamesAsync(command, cancellationToken);
    }

    private static async Task<HashSet<string>> ExecuteTableNamesAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            tableNames.Add(reader.GetString(0));

        return tableNames;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsNpgsql(RunnerRunnerAuthDbContext db) =>
        string.Equals(db.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);

    private sealed record AuthTable(string Schema, string Name);
}
