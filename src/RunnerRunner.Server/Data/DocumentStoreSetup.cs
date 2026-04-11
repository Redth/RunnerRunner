using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using HostModel = RunnerRunner.Core.Models.Host;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Data;

public static class DocumentStoreSetup
{
    public static IServiceCollection AddRunnerRunnerDocumentStore(
        this IServiceCollection services,
        string connectionString = "Data Source=runnerrunner.db")
    {
        services.AddSqliteDocumentStore(opts =>
        {
            opts.DatabaseProvider = new SqliteDatabaseProvider(connectionString);

            // Give each entity type its own table for cleaner organization
            opts.MapTypeToTable<HostModel>("hosts");
            opts.MapTypeToTable<RunnerProfile>("runner_profiles");
            opts.MapTypeToTable<RunnerInstance>("runner_instances");
            opts.MapTypeToTable<RunnerAssignment>("runner_assignments");
            opts.MapTypeToTable<EnvironmentVariableSet>("env_var_sets");
            opts.MapTypeToTable<RunnerAgentVersion>("runner_agent_versions");
            opts.MapTypeToTable<ProviderCredential>("provider_credentials");
            opts.MapTypeToTable<AuditLogEntry>("audit_log");
        });

        return services;
    }
}
