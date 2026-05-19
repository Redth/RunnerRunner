using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerLocalUpdateStoreTests
{
    [Theory]
    [InlineData(null, true, HostWorkerUpdateSourceKind.Release)]
    [InlineData("", true, HostWorkerUpdateSourceKind.Release)]
    [InlineData("github_releases", true, HostWorkerUpdateSourceKind.Release)]
    [InlineData(" LOCAL ", true, HostWorkerUpdateSourceKind.LocalFolder)]
    [InlineData("ssh-local-folder", true, HostWorkerUpdateSourceKind.LocalFolder)]
    [InlineData("unknown", false, HostWorkerUpdateSourceKind.Release)]
    public void SourceKinds_TryParse_HandlesKnownAliases(
        string? value,
        bool expectedResult,
        HostWorkerUpdateSourceKind expectedSource)
    {
        var result = HostWorkerUpdateSourceKinds.TryParse(value, out var source);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedSource, source);
    }

    [Fact]
    public void SourceKinds_DisplayValues_AreStable()
    {
        Assert.Equal("release", HostWorkerUpdateSourceKind.Release.ToSourceId());
        Assert.Equal("GitHub ref", HostWorkerUpdateSourceKind.Release.ToDisplayName());
        Assert.Equal("local-folder", HostWorkerUpdateSourceKind.LocalFolder.ToSourceId());
        Assert.Equal("Local folder", HostWorkerUpdateSourceKind.LocalFolder.ToDisplayName());
        Assert.Equal("999", ((HostWorkerUpdateSourceKind)999).ToSourceId());
        Assert.Equal("999", ((HostWorkerUpdateSourceKind)999).ToDisplayName());
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
    public void ListVersions_DiscoversRootArtifactsAsLocalVersion()
    {
        using var fixture = StoreFixture.Create();
        File.WriteAllBytes(Path.Combine(fixture.LocalRoot, "runnerrunner-hostworker-linux-x64.tar.gz"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(fixture.LocalRoot, "runnerrunner-hostworker-win-x64.tar.gz"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(fixture.LocalRoot, "runnerrunner-hostworker-linux-x64.zip"), [7, 8, 9]);

        var version = Assert.Single(fixture.Store.ListVersions(HostWorkerUpdateSourceKind.LocalFolder));

        Assert.Equal("local", version.Version);
        var artifact = Assert.Single(version.Assets);
        Assert.Equal("linux-x64", artifact.RuntimeIdentifier);
        Assert.Equal("runnerrunner-hostworker-linux-x64.tar.gz", artifact.AssetName);
    }

    [Fact]
    public void GetVersion_ReturnsLatestVersionForEmptyVersion()
    {
        using var fixture = StoreFixture.Create();
        var olderDirectory = Path.Combine(fixture.LocalRoot, "older");
        var newerDirectory = Path.Combine(fixture.LocalRoot, "newer");
        Directory.CreateDirectory(olderDirectory);
        Directory.CreateDirectory(newerDirectory);
        var olderArtifact = Path.Combine(olderDirectory, "runnerrunner-hostworker-win-x64.zip");
        var newerArtifact = Path.Combine(newerDirectory, "runnerrunner-hostworker-win-x64.zip");
        File.WriteAllBytes(olderArtifact, [1]);
        File.WriteAllBytes(newerArtifact, [2]);
        File.SetLastWriteTimeUtc(olderArtifact, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newerArtifact, DateTime.UtcNow);

        var version = fixture.Store.GetVersion(HostWorkerUpdateSourceKind.LocalFolder, " ");

        Assert.NotNull(version);
        Assert.Equal("newer", version.Version);
    }

    [Fact]
    public async Task GetArtifactAsync_ReturnsLocalArtifactWithNormalizedAssetName()
    {
        using var fixture = StoreFixture.Create();
        File.WriteAllBytes(Path.Combine(fixture.LocalRoot, "runnerrunner-hostworker-win-x64.zip"), [1, 2, 3]);

        var artifact = await fixture.Store.GetArtifactAsync(
            HostWorkerUpdateSourceKind.LocalFolder,
            "local",
            "RUNNERRUNNER-HOSTWORKER-WIN-X64.ZIP");

        Assert.NotNull(artifact);
        Assert.Equal("win-x64", artifact.RuntimeIdentifier);
        Assert.Equal("runnerrunner-hostworker-win-x64.zip", artifact.AssetName);
        Assert.Equal(3, artifact.SizeBytes);
    }

    [Fact]
    public async Task WriteArtifactAsync_CopiesArtifactContents()
    {
        using var fixture = StoreFixture.Create();
        File.WriteAllBytes(Path.Combine(fixture.LocalRoot, "runnerrunner-hostworker-win-x64.zip"), [1, 2, 3]);
        var artifact = await fixture.Store.GetArtifactAsync(
            HostWorkerUpdateSourceKind.LocalFolder,
            "local",
            "runnerrunner-hostworker-win-x64.zip");
        Assert.NotNull(artifact);
        using var output = new MemoryStream();

        await fixture.Store.WriteArtifactAsync(artifact, output);

        Assert.Equal([1, 2, 3], output.ToArray());
    }

    [Fact]
    public void BuildDownloadUrl_CombinesEscapedArtifactPathAndHash()
    {
        using var fixture = StoreFixture.Create();
        var artifact = new HostWorkerUpdateArtifact(
            HostWorkerUpdateSourceKind.LocalFolder,
            "dev build",
            "win-x64",
            "runnerrunner-hostworker-win-x64.zip",
            "abc+123",
            10,
            DateTimeOffset.UtcNow);

        var url = fixture.Store.BuildDownloadUrl(artifact, "https://example.test/base");

        Assert.Equal(
            "https://example.test/base/api/hostworker-updates/artifacts/local-folder/dev%20build/runnerrunner-hostworker-win-x64.zip?sha256=abc%2B123",
            url);
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
