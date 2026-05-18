using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using RunnerRunner.Server.Tests.TestSupport;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerUpdateServiceTests
{
    [Fact]
    public async Task GetReleaseAsync_UsesStoredGitHubCredentialForRefArtifacts()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new ProviderCredential
        {
            Id = "github-cred",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Redth",
            GitHubToken = "stored-token"
        });

        var capturedAuthorizations = new List<string?>();
        using var fixture = CreateService(store, request =>
        {
            capturedAuthorizations.Add(request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/repos/Redth/RunnerRunner/releases/tags/feature", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (path.EndsWith("/repos/Redth/RunnerRunner/commits/feature", StringComparison.Ordinal))
                return JsonResponse("""{"sha":"abc123","html_url":"https://github.com/Redth/RunnerRunner/commit/abc123"}""");

            if (path.EndsWith("/repos/Redth/RunnerRunner/actions/runs", StringComparison.Ordinal))
                return JsonResponse("""{"workflow_runs":[{"id":123,"html_url":"https://github.com/Redth/RunnerRunner/actions/runs/123","conclusion":"success","created_at":"2026-05-16T00:00:00Z","updated_at":"2026-05-16T00:05:00Z"}]}""");

            if (path.EndsWith("/repos/Redth/RunnerRunner/actions/runs/123/artifacts", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {"artifacts":[
                      {"name":"runnerrunner-hostworker-manifest","archive_download_url":"https://api.github.com/repos/Redth/RunnerRunner/actions/artifacts/1/zip","expired":false},
                      {"name":"runnerrunner-hostworker-assets","archive_download_url":"https://api.github.com/repos/Redth/RunnerRunner/actions/artifacts/2/zip","expired":false}
                    ]}
                    """);
            }

            if (path.EndsWith("/repos/Redth/RunnerRunner/actions/artifacts/1/zip", StringComparison.Ordinal))
            {
                return ZipResponse(
                    "release-manifest.json",
                    """{"gitSha":"abc123","assets":[{"name":"runnerrunner-hostworker-linux-x64.tar.gz","sha256":"asset-sha"}],"images":{}}""");
            }

            throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
        });

        var release = await fixture.Service.GetReleaseAsync("feature", forceRefresh: true);

        Assert.NotNull(release);
        Assert.Equal("abc123", release.Version);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("runnerrunner-hostworker-linux-x64.tar.gz", asset.Name);
        Assert.Contains("/api/hostworker-updates/github-artifacts/123/runnerrunner-hostworker-assets/", asset.DownloadUrl);
        Assert.All(capturedAuthorizations, authorization => Assert.Equal("Bearer stored-token", authorization));
    }

    [Fact]
    public async Task ExtractGitHubActionsArtifactAssetAsync_UsesStoredGitHubCredentialForAssetDownload()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new ProviderCredential
        {
            Id = "github-cred",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Redth",
            GitHubToken = "stored-token"
        });

        var capturedAuthorizations = new List<string?>();
        using var fixture = CreateService(store, request =>
        {
            capturedAuthorizations.Add(request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/repos/Redth/RunnerRunner/actions/runs/123/artifacts", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {"artifacts":[
                      {"name":"runnerrunner-hostworker-assets","archive_download_url":"https://api.github.com/repos/Redth/RunnerRunner/actions/artifacts/2/zip","expired":false}
                    ]}
                    """);
            }

            if (path.EndsWith("/repos/Redth/RunnerRunner/actions/artifacts/2/zip", StringComparison.Ordinal))
                return ZipResponse("runnerrunner-hostworker-linux-x64.tar.gz", "hostworker-payload");

            throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
        });

        var outputPath = Path.Combine(fixture.Root, "rr-hostworker-asset.tar.gz");
        try
        {
            await fixture.Service.ExtractGitHubActionsArtifactAssetAsync(
                123,
                "runnerrunner-hostworker-assets",
                "runnerrunner-hostworker-linux-x64.tar.gz",
                outputPath);

            Assert.Equal("hostworker-payload", await File.ReadAllTextAsync(outputPath));
            Assert.All(capturedAuthorizations, authorization => Assert.Equal("Bearer stored-token", authorization));
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task QueueUpdateAsync_DispatchesSelectedAssetUpdateCommand()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new ProviderCredential
        {
            Id = "github-cred",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Redth",
            GitHubToken = "stored-token"
        });
        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "linux-host",
            Platform = HostPlatform.Linux,
            Architecture = "x64",
            AgentStatus = AgentStatus.Online,
            AgentVersion = "1.0.0"
        });

        using var fixture = CreateService(store, request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/repos/Redth/RunnerRunner/releases/latest", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "tag_name": "v2.0.0",
                      "html_url": "https://github.com/Redth/RunnerRunner/releases/tag/v2.0.0",
                      "published_at": "2026-05-16T00:00:00Z",
                      "assets": [
                        { "name": "release-manifest.json", "browser_download_url": "https://downloads.example.test/release-manifest.json" },
                        { "name": "runnerrunner-hostworker-linux-x64.tar.gz", "browser_download_url": "https://downloads.example.test/runnerrunner-hostworker-linux-x64.tar.gz" }
                      ]
                    }
                    """);
            }

            if (uri.AbsoluteUri == "https://downloads.example.test/release-manifest.json")
            {
                return JsonResponse("""
                    {
                      "assets": [
                        { "name": "runnerrunner-hostworker-linux-x64.tar.gz", "sha256": "asset-sha" }
                      ],
                      "images": {}
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
        });

        await fixture.Service.QueueUpdateAsync("host-1", HostWorkerUpdateSelection.LatestRelease(force: true));

        var dispatched = Assert.Single(fixture.Dispatcher.Commands);
        Assert.Equal("host-1", dispatched.HostId);
        Assert.Equal(HostCommandKind.ApplyHostWorkerUpdate, dispatched.Kind);
        var command = Assert.IsType<HostWorkerUpdateCommand>(dispatched.Command);
        Assert.Equal("v2.0.0", command.TargetVersion);
        Assert.Equal("runnerrunner-hostworker-linux-x64.tar.gz", command.AssetName);
        Assert.Equal("https://downloads.example.test/runnerrunner-hostworker-linux-x64.tar.gz", command.AssetUrl);
        Assert.Equal("asset-sha", command.Sha256);
        Assert.True(command.Force);
    }

    private static ServiceFixture CreateService(
        Shiny.DocumentDb.IDocumentStore store,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "TestResults",
            $"rr-hostworker-updates-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorkerUpdates:Repository"] = "Redth/RunnerRunner",
                ["HostWorkerUpdates:PublicBaseUrl"] = "https://runner.example.test/",
                ["HostWorkerUpdates:StorageRoot"] = root
            })
            .Build();
        var httpFactory = new FakeProviderHttpApi(handler);
        var dispatcher = new RecordingHostCommandDispatcher();

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(root);

        var service = new HostWorkerUpdateService(
            httpFactory,
            configuration,
            new GitHubAuthenticationService(httpFactory, NullLogger<GitHubAuthenticationService>.Instance),
            store,
            dispatcher,
            new HostWorkerLocalUpdateStore(
                configuration,
                environment,
                NullLogger<HostWorkerLocalUpdateStore>.Instance),
            NullLogger<HostWorkerUpdateService>.Instance);

        return new ServiceFixture(root, service, dispatcher, httpFactory);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage ZipResponse(string entryName, string content)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(memory.ToArray())
        };
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly FakeProviderHttpApi _httpFactory;

        public ServiceFixture(
            string root,
            HostWorkerUpdateService service,
            RecordingHostCommandDispatcher dispatcher,
            FakeProviderHttpApi httpFactory)
        {
            Root = root;
            Service = service;
            Dispatcher = dispatcher;
            _httpFactory = httpFactory;
        }

        public string Root { get; }
        public HostWorkerUpdateService Service { get; }
        public RecordingHostCommandDispatcher Dispatcher { get; }

        public void Dispose()
        {
            _httpFactory.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
