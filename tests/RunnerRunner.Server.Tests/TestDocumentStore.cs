using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests;

/// <summary>
/// Creates a fresh in-memory SQLite document store for each test.
/// </summary>
public static class TestDocumentStore
{
    public static IDocumentStore Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"rr-test-{Guid.NewGuid():N}.db");
        var store = new SqliteDocumentStore(new DocumentStoreOptions
        {
            DatabaseProvider = new SqliteDatabaseProvider($"Data Source={dbPath}")
        }
        .MapTypeToTable<Host>("hosts")
        .MapTypeToTable<RunnerProfile>("runner_profiles")
        .MapTypeToTable<RunnerInstance>("runner_instances")
        .MapTypeToTable<RunnerAssignment>("runner_assignments")
        .MapTypeToTable<EnvironmentVariableSet>("env_var_sets")
        .MapTypeToTable<RunnerAgentVersion>("runner_agent_versions")
        .MapTypeToTable<ProviderCredential>("provider_credentials")
        .MapTypeToTable<AuditLogEntry>("audit_log")
        .MapTypeToTable<WebhookEvent>("webhook_events"));

        return store;
    }
}
