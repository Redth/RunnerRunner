using Shiny.DocumentDb;
using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Data;

/// <summary>
/// Ensures all document store tables are created at startup.
/// Shiny DocumentDB creates tables lazily on first write operation,
/// but read queries against non-existent tables throw.
/// We insert and immediately remove a sentinel record to trigger
/// table creation without affecting existing data.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task EnsureTablesCreatedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await EnsureTable<Host>(store, () => new Host { Id = "__init__", Name = "__init__" });
        await EnsureTable<RunnerProfile>(store, () => new RunnerProfile { Id = "__init__", Name = "__init__" });
        await EnsureTable<RunnerInstance>(store, () => new RunnerInstance { Id = "__init__", RunnerName = "__init__" });
        await EnsureTable<RunnerAssignment>(store, () => new RunnerAssignment { Id = "__init__" });
        await EnsureTable<EnvironmentVariableSet>(store, () => new EnvironmentVariableSet { Id = "__init__", Name = "__init__" });
        await EnsureTable<RunnerAgentVersion>(store, () => new RunnerAgentVersion { Id = "__init__", Version = "__init__" });
        await EnsureTable<ProviderCredential>(store, () => new ProviderCredential { Id = "__init__", Name = "__init__" });
        await EnsureTable<RegistryCredential>(store, () => new RegistryCredential { Id = "__init__", Name = "__init__", RegistryUrl = "__init__" });
        await EnsureTable<AgentImage>(store, () => new AgentImage { Id = "__init__", HostId = "__init__", Repository = "__init__" });
        await EnsureTable<AuditLogEntry>(store, () => new AuditLogEntry { Id = "__init__", Action = "__init__", EntityType = "__init__" });
        await EnsureTable<WebhookEvent>(store, () => new WebhookEvent { Id = "__init__" });
        await EnsureTable<WebhookBinding>(store, () => new WebhookBinding { Id = "__init__", Name = "__init__" });
        await EnsureTable<ProvisioningRule>(store, () => new ProvisioningRule { Id = "__init__", Name = "__init__" });

        await MigrateLegacyWebhookBindings(store);
        await CleanupLegacyRunnerInstances(store);
    }

    private static async Task EnsureTable<T>(IDocumentStore store, Func<T> createSentinel) where T : class
    {
        try
        {
            // Try a read first — if it works, table exists
            await store.Get<T>("__init__");
        }
        catch
        {
            // Table doesn't exist — insert and remove a sentinel to create it
            try
            {
                var sentinel = createSentinel();
                await store.Insert(sentinel);
                await store.Remove<T>("__init__");
            }
            catch
            {
                // Table might have been created by another instance
            }
        }
    }

    private static async Task CleanupLegacyRunnerInstances(IDocumentStore store)
    {
        var staleInstances = (await store.Query<RunnerInstance>().ToList())
            .Where(x =>
                string.IsNullOrWhiteSpace(x.ProfileId) ||
                string.IsNullOrWhiteSpace(x.HostId) ||
                string.IsNullOrWhiteSpace(x.RunnerName))
            .ToList();

        foreach (var instance in staleInstances)
            await store.Remove<RunnerInstance>(instance.Id);
    }

    private static async Task MigrateLegacyWebhookBindings(IDocumentStore store)
    {
        var legacyBindings = (await store.Query<WebhookBinding>().ToList()).ToList();
        if (legacyBindings.Count == 0)
            return;

        var existingRules = (await store.Query<ProvisioningRule>().ToList()).ToList();

        foreach (var binding in legacyBindings)
        {
            var alreadyMigrated = existingRules.Any(rule =>
                rule.Type == ProvisioningType.Webhook &&
                string.Equals(rule.Name, binding.Name, StringComparison.OrdinalIgnoreCase) &&
                rule.Provider == binding.Provider &&
                string.Equals(rule.ProviderCredentialId ?? "", binding.ProviderCredentialId ?? "", StringComparison.Ordinal));

            if (alreadyMigrated)
                continue;

            var defaultProfileId = binding.DefaultProfileId
                ?? binding.Mappings.OrderByDescending(x => x.Priority)
                    .Select(x => x.ProfileId)
                    .FirstOrDefault()
                ?? "";

            var migratedRule = new ProvisioningRule
            {
                Name = binding.Name,
                Description = "Migrated from legacy webhook binding",
                Type = ProvisioningType.Webhook,
                ProfileId = defaultProfileId,
                DefaultProfileId = binding.DefaultProfileId,
                LabelMappings = [.. binding.Mappings],
                AllowedOrgs = [.. binding.AllowedOrgs],
                AllowedRepos = [.. binding.AllowedRepos],
                WebhookSecret = binding.WebhookSecret,
                MaxConcurrent = binding.MaxConcurrentJobs,
                CooldownSeconds = binding.CooldownSeconds,
                Provider = binding.Provider,
                ProviderCredentialId = binding.ProviderCredentialId,
                Enabled = binding.Enabled,
                CreatedAt = binding.CreatedAt,
                UpdatedAt = DateTime.UtcNow
            };

            await store.Insert(migratedRule);
            existingRules.Add(migratedRule);
        }
    }
}
