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
        var options = new DocumentStoreOptions
        {
            DatabaseProvider = new PostgreSqlDatabaseProvider(connectionString)
        };

        // Give each entity type its own table for cleaner organization
        options.MapTypeToTable<HostModel>("hosts");
        options.MapTypeToTable<RunnerProfile>("runner_profiles");
        options.MapTypeToTable<RunnerInstance>("runner_instances");
        options.MapTypeToTable<RunnerAssignment>("runner_assignments");
        options.MapTypeToTable<EnvironmentVariableSet>("env_var_sets");
        options.MapTypeToTable<RunnerAgentVersion>("runner_agent_versions");
        options.MapTypeToTable<ProviderCredential>("provider_credentials");
        options.MapTypeToTable<RegistryCredential>("registry_credentials");
        options.MapTypeToTable<AgentImage>("agent_images");
        options.MapTypeToTable<AuditLogEntry>("audit_log");
        options.MapTypeToTable<WebhookEvent>("webhook_events");
        options.MapTypeToTable<WebhookBinding>("webhook_bindings");
        options.MapTypeToTable<ProvisioningRule>("provisioning_rules");
        options.MapTypeToTable<RunnerInitStepDefinition>("runner_init_steps");
        options.MapTypeToTable<RunnerRunnerAuthSettings>("auth_settings");

        services.AddSingleton(options);
        services.AddSingleton<IDocumentStore, ResilientDocumentStore>();

        return services;
    }
}
