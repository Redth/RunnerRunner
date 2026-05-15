using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerLocalUpdateStoreTests
{
    [Fact]
    public async Task SaveUploadAsync_StoresVersionedArtifactWithChecksum()
    {
        using var fixture = StoreFixture.Create();
        var store = fixture.Store;
        var bytes = "hostworker-build"u8.ToArray();

        var artifact = await store.SaveUploadAsync(
            "dev-abc123",
            "runnerrunner-hostworker-osx-arm64.tar.gz",
            new MemoryStream(bytes),
            bytes.Length);

        var versions = store.ListVersions(HostWorkerUpdateSourceKind.Upload);

        Assert.Equal(HostWorkerUpdateSourceKind.Upload, artifact.Source);
        Assert.Equal("dev-abc123", artifact.Version);
        Assert.Equal("osx-arm64", artifact.RuntimeIdentifier);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), artifact.Sha256);
        Assert.Single(versions);
        Assert.Equal("dev-abc123", versions[0].Version);
        Assert.Equal("runnerrunner-hostworker-osx-arm64.tar.gz", versions[0].Assets[0].AssetName);
    }

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
    public async Task SaveUploadAsync_RejectsPathTraversalAssetName()
    {
        using var fixture = StoreFixture.Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.SaveUploadAsync("dev", "../runnerrunner-hostworker-linux-x64.tar.gz", new MemoryStream([1]), 1));

        Assert.Contains("Unsupported HostWorker update artifact", ex.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void EmptyPathRoots_UseStorageRootDefaults(string? configuredUploadRoot, string? configuredLocalArtifactRoot)
    {
        using var fixture = StoreFixture.Create(configuredUploadRoot, configuredLocalArtifactRoot);

        Assert.Equal(Path.Combine(fixture.Root, "uploads"), fixture.Store.UploadRoot);
        Assert.Equal(Path.Combine(fixture.Root, "local"), fixture.Store.LocalFolderRoot);
        Assert.True(Directory.Exists(fixture.Store.UploadRoot));
        Assert.True(Directory.Exists(fixture.Store.LocalFolderRoot));
    }

    [Fact]
    public void ConfiguredPathRoots_AreUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rr-hostworker-updates-{Guid.NewGuid():N}");
        var uploadRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-upload-{Guid.NewGuid():N}");
        var localRoot = Path.Combine(Path.GetTempPath(), $"rr-hostworker-local-{Guid.NewGuid():N}");

        try
        {
            using var fixture = StoreFixture.Create(uploadRoot, localRoot, root);

            Assert.Equal(uploadRoot, fixture.Store.UploadRoot);
            Assert.Equal(localRoot, fixture.Store.LocalFolderRoot);
            Assert.True(Directory.Exists(uploadRoot));
            Assert.True(Directory.Exists(localRoot));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            if (Directory.Exists(uploadRoot))
                Directory.Delete(uploadRoot, recursive: true);

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
            string? uploadRoot = null,
            string? localArtifactRoot = null,
            string? storageRoot = null)
        {
            var root = storageRoot ?? Path.Combine(Path.GetTempPath(), $"rr-hostworker-updates-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HostWorkerUpdates:StorageRoot"] = root,
                    ["HostWorkerUpdates:UploadRoot"] = uploadRoot,
                    ["HostWorkerUpdates:LocalArtifactRoot"] = localArtifactRoot
                })
                .Build();
            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(root);

            var store = new HostWorkerLocalUpdateStore(
                configuration,
                environment,
                NullLogger<HostWorkerLocalUpdateStore>.Instance);

            return new StoreFixture(root, store);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
