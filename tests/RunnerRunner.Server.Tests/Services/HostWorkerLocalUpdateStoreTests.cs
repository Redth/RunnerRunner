using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerLocalUpdateStoreTests
{
    [Fact]
    public void ListVersions_DiscoversLocalFolderArtifacts()
    {
        using var fixture = StoreFixture.Create();
        var versionDirectory = Path.Combine(fixture.LocalRoot, "dev-local");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllBytes(Path.Combine(versionDirectory, "runnerrunner-hostworker-win-x64.zip"), [1, 2, 3]);

        var versions = fixture.Store.ListVersions(HostWorkerUpdateSourceKind.LocalFolder);

        Assert.Single(versions);
        Assert.Equal("dev-local", versions[0].Version);
        Assert.Equal("win-x64", versions[0].Assets[0].RuntimeIdentifier);
    }

    [Fact]
    public async Task GetArtifactAsync_RejectsPathTraversalAssetName()
    {
        using var fixture = StoreFixture.Create();

        var artifact = await fixture.Store.GetArtifactAsync(
            HostWorkerUpdateSourceKind.LocalFolder,
            "dev",
            "../runnerrunner-hostworker-linux-x64.tar.gz");

        Assert.Null(artifact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPathRoots_UseStorageRootDefaults(string? configuredLocalArtifactRoot)
    {
        using var fixture = StoreFixture.Create(configuredLocalArtifactRoot);

        Assert.Equal(Path.Combine(fixture.Root, "local"), fixture.Store.LocalFolderRoot);
        Assert.True(Directory.Exists(fixture.Store.LocalFolderRoot));
    }

    [Fact]
    public void ConfiguredPathRoots_AreUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rr-hostworker-updates-{Guid.NewGuid():N}");
        var localRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-local-{Guid.NewGuid():N}");

        try
        {
            using var fixture = StoreFixture.Create(localRoot, root);

            Assert.Equal(localRoot, fixture.Store.LocalFolderRoot);
            Assert.True(Directory.Exists(localRoot));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            if (Directory.Exists(localRoot))
                Directory.Delete(localRoot, recursive: true);
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string root, HostWorkerLocalUpdateStore store)
        {
            Root = root;
            Store = store;
            LocalRoot = Path.Combine(root, "local");
        }

        public string Root { get; }
        public string LocalRoot { get; }
        public HostWorkerLocalUpdateStore Store { get; }

        public static StoreFixture Create(
            string? localArtifactRoot = null,
            string? storageRoot = null)
        {
            var root = storageRoot ?? Path.Combine(Path.GetTempPath(), $"rr-hostworker-updates-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HostWorkerUpdates:StorageRoot"] = root,
                    ["HostWorkerUpdates:LocalArtifactRoot"] = localArtifactRoot
                })
                .Build();
            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(root);

            var store = new HostWorkerLocalUpdateStore(
                configuration,
                environment);

            return new StoreFixture(root, store);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
