using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class RegistryCredentialResolverTests
{
    [Theory]
    [InlineData(ExecutionBackend.Tart, RegistryType.Tart)]
    [InlineData(ExecutionBackend.Docker, RegistryType.Docker)]
    public async Task ResolvesOnlyTheSelectedBackendCredential(ExecutionBackend backend, RegistryType expected)
    {
        var store = TestDocumentStore.Create();
        foreach (var type in new[] { RegistryType.Docker, RegistryType.Tart })
            await store.Insert(new RegistryCredential
            {
                Id = type.ToString(), Name = type.ToString(), RegistryUrl = "https://ghcr.io/",
                RegistryType = type, DefaultNamespace = "example-org", Username = "fixture",
                Password = "fixture-" + type
            });

        var result = await RegistryCredentialResolver.ResolveAsync(store, backend,
            backend == ExecutionBackend.Docker ? new DockerImageConfig { RegistryUrl = "ghcr.io", ImageName = "example-org/windows" } : null,
            backend == ExecutionBackend.Tart ? new TartImageConfig { RegistryUrl = "ghcr.io", ImageName = "example-org/macos" } : null);

        Assert.NotNull(result);
        Assert.Equal(expected, result.RegistryType);
        Assert.Equal("fixture-" + expected, result.Password);
    }

    [Theory]
    [InlineData(RegistryType.Tart, "ghcr.io")]
    [InlineData(RegistryType.Docker, "registry.example.com")]
    public async Task ExplicitCredentialCannotCrossRegistryOrBackend(RegistryType type, string registry)
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new RegistryCredential
        {
            Id = "explicit", Name = "Other scope", RegistryType = type, RegistryUrl = registry,
            Username = "fixture", Password = "fixture-secret"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => RegistryCredentialResolver.ResolveAsync(
            store, ExecutionBackend.Docker,
            new DockerImageConfig { RegistryUrl = "ghcr.io", ImageName = "example-org/windows", CredentialId = "explicit" }, null));
    }
}
