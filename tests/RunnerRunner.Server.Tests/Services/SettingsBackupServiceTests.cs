using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class SettingsBackupServiceTests
{
    [Fact]
    public async Task ExportImportBackup_RestoresConfigurationDocumentsAndSecrets()
    {
        var source = TestDocumentStore.Create();
        var destination = TestDocumentStore.Create();
        var ruleId = "rule-1";
        var profileId = "profile-1";
        var credentialId = "credential-1";
        var registryId = "registry-1";
        var envSetId = "env-1";
        var stepId = "step-1";

        await source.Insert(new Host
        {
            Id = "host-1",
            Name = "mac-host",
            DisplayName = "Mac Host",
            Platform = HostPlatform.MacOS,
            GroupId = "macs",
            Capabilities = ["native"],
            Labels = { ["os"] = "macos" }
        });
        await source.Insert(new ProviderCredential
        {
            Id = credentialId,
            Name = "github app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "123",
            GitHubAppPrivateKey = "private-key",
            GitHubAppWebhookSecret = "webhook-secret"
        });
        await source.Insert(new RegistryCredential
        {
            Id = registryId,
            Name = "ghcr",
            RegistryUrl = "ghcr.io",
            Username = "robot",
            Password = "registry-secret"
        });
        await source.Insert(new EnvironmentVariableSet
        {
            Id = envSetId,
            Name = "shared",
            Variables = { ["TOKEN"] = "secret-token" },
            SecretKeys = ["TOKEN"]
        });
        await source.Insert(new RunnerInitStepDefinition
        {
            Id = stepId,
            Name = "prepare",
            Script = "echo preparing"
        });
        await source.Insert(new RunnerProfile
        {
            Id = profileId,
            Name = "mac profile",
            Provider = RunnerProvider.GitHubActions,
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Native,
            EnvironmentVariableSetIds = [envSetId]
        });
        await source.Insert(new ProvisioningRule
        {
            Id = ruleId,
            Name = "mac webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = credentialId,
            TargetGroupId = "macs",
            RunnerDefinitions =
            [
                new RunnerDefinition
                {
                    Id = profileId,
                    Name = "mac native",
                    RequiredHostPlatform = HostPlatform.MacOS,
                    ExecutionBackend = ExecutionBackend.Native,
                    EnvironmentVariableSetIds = [envSetId],
                    InitStepRefs = [new RunnerInitStepRef { InitStepId = stepId }]
                }
            ]
        });

        var exportService = CreateService(source);
        var importService = CreateService(destination);

        var exported = await exportService.ExportBackupAsync(password: null);
        await using var importStream = new MemoryStream(exported.Bytes);
        var result = await importService.ImportBackupAsync(importStream, password: null);

        Assert.Equal("application/zip", exported.ContentType);
        Assert.Equal(6, result.TotalImported);
        Assert.Equal(1, result.ProvisioningRules);
        Assert.Equal(1, result.ProviderCredentials);
        Assert.Equal(1, result.RegistryCredentials);
        Assert.Equal(1, result.EnvironmentVariableSets);
        Assert.Equal(1, result.CustomSteps);
        Assert.Equal(1, result.RunnerProfiles);
        Assert.Equal(1, result.HostHints);

        var restoredCredential = await destination.Get<ProviderCredential>(credentialId);
        Assert.NotNull(restoredCredential);
        Assert.Equal("webhook-secret", restoredCredential.GitHubAppWebhookSecret);
        var restoredRegistry = await destination.Get<RegistryCredential>(registryId);
        Assert.NotNull(restoredRegistry);
        Assert.Equal("registry-secret", restoredRegistry.Password);
        var restoredRule = await destination.Get<ProvisioningRule>(ruleId);
        Assert.NotNull(restoredRule);
        Assert.Equal("macs", restoredRule.TargetGroupId);
    }

    [Fact]
    public async Task ExportImportBackup_RequiresPasswordForEncryptedBundle()
    {
        var source = TestDocumentStore.Create();
        var destination = TestDocumentStore.Create();
        await source.Insert(new ProvisioningRule
        {
            Id = "rule-1",
            Name = "linux webhook",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions
        });

        var exported = await CreateService(source).ExportBackupAsync("correct horse battery staple");

        Assert.Equal("application/octet-stream", exported.ContentType);
        await using var missingPasswordStream = new MemoryStream(exported.Bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(destination).ImportBackupAsync(missingPasswordStream, password: null));

        await using var importStream = new MemoryStream(exported.Bytes);
        var result = await CreateService(destination).ImportBackupAsync(importStream, "correct horse battery staple");

        Assert.Equal(1, result.ProvisioningRules);
        Assert.NotNull(await destination.Get<ProvisioningRule>("rule-1"));
    }

    private static SettingsBackupService CreateService(Shiny.DocumentDb.IDocumentStore store) =>
        new(store, NullLogger<SettingsBackupService>.Instance);
}
