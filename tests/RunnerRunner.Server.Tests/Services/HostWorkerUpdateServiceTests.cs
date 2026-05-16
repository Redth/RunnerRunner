using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using RunnerRunner.Server.Tests.Providers;

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

        var outputPath = Path.Combine(Path.GetTempPath(), $"rr-hostworker-asset-{Guid.NewGuid():N}.tar.gz");
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

    private static ServiceFixture CreateService(
        Shiny.DocumentDb.IDocumentStore store,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rr-hostworker-updates-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorkerUpdates:Repository"] = "Redth/RunnerRunner",
                ["HostWorkerUpdates:PublicBaseUrl"] = "https://runner.example.test/",
                ["HostWorkerUpdates:StorageRoot"] = root
            })
            .Build();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new FakeHttpHandler(handler)));

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(root);

        var service = new HostWorkerUpdateService(
            httpFactory,
            configuration,
            new GitHubAuthenticationService(httpFactory, NullLogger<GitHubAuthenticationService>.Instance),
            store,
            Substitute.For<IHostCommandDispatcher>(),
            new HostWorkerLocalUpdateStore(
                configuration,
                environment,
                NullLogger<HostWorkerLocalUpdateStore>.Instance),
            NullLogger<HostWorkerUpdateService>.Instance);

        return new ServiceFixture(root, service);
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
        public ServiceFixture(string root, HostWorkerUpdateService service)
        {
            Root = root;
            Service = service;
        }

        private string Root { get; }
        public HostWorkerUpdateService Service { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
