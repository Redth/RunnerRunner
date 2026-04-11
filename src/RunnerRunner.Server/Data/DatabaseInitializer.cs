using Shiny.DocumentDb;
using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Data;

/// <summary>
/// Ensures all document store tables are created at startup.
/// Shiny DocumentDB creates tables lazily on first write operation,
/// but read queries against non-existent tables throw. We use Clear()
/// which is a write op (DELETE FROM) that triggers CREATE TABLE IF NOT EXISTS
/// without affecting existing data.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task EnsureTablesCreatedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await store.Clear<Host>();
        await store.Clear<RunnerProfile>();
        await store.Clear<RunnerInstance>();
        await store.Clear<RunnerAssignment>();
        await store.Clear<EnvironmentVariableSet>();
        await store.Clear<RunnerAgentVersion>();
        await store.Clear<ProviderCredential>();
        await store.Clear<RegistryCredential>();
        await store.Clear<AgentImage>();
        await store.Clear<AuditLogEntry>();
    }
}
