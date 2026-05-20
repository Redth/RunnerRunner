using Shiny.DocumentDb;
using Shiny.DocumentDb.PostgreSql;
using HostModel = RunnerRunner.Core.Models.Host;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Models;

namespace RunnerRunner.Server.Data;

public static class DocumentStoreSetup
{
    public static IServiceCollection AddRunnerRunnerDocumentStore(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddPostgreSqlDocumentStore(opts =>
        {
            opts.DatabaseProvider = new PostgreSqlDatabaseProvider(connectionString);

            // Give each entity type its own table for cleaner organization
            opts.MapTypeToTable<HostModel>("hosts");
            opts.MapTypeToTable<RunnerProfile>("runner_profiles");
            opts.MapTypeToTable<RunnerInstance>("runner_instances");
            opts.MapTypeToTable<RunnerAssignment>("runner_assignments");
            opts.MapTypeToTable<EnvironmentVariableSet>("env_var_sets");
            opts.MapTypeToTable<RunnerAgentVersion>("runner_agent_versions");
            opts.MapTypeToTable<ProviderCredential>("provider_credentials");
            opts.MapTypeToTable<RegistryCredential>("registry_credentials");
            opts.MapTypeToTable<AgentImage>("agent_images");
            opts.MapTypeToTable<AuditLogEntry>("audit_log");
            opts.MapTypeToTable<WebhookEvent>("webhook_events");
            opts.MapTypeToTable<WebhookBinding>("webhook_bindings");
            opts.MapTypeToTable<ProvisioningRule>("provisioning_rules");
            opts.MapTypeToTable<RunnerRunnerAuthSettings>("auth_settings");
        });

        return services;
    }
}
