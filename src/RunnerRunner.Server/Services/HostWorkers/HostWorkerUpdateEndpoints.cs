using System.Security.Cryptography;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services.HostWorkers;

public static class HostWorkerUpdateEndpoints
{
    public static IEndpointRouteBuilder MapHostWorkerUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/hostworker-updates");

        group.MapGet("/", async (
            HttpRequest request,
            IDocumentStore store,
            IConfiguration configuration,
            HostWorkerUpdateService updateService,
            string? source,
            CancellationToken ct) =>
        {
            if (!await IsAuthorizedAsync(request, store, configuration, ct))
                return Results.Unauthorized();

            if (!HostWorkerUpdateSourceKinds.TryParse(source, out var sourceKind))
                return Results.BadRequest(new { error = $"Unsupported HostWorker update source '{source}'." });

            var versions = await updateService.GetAvailableVersionsAsync(sourceKind, ct);
            return Results.Ok(new
            {
                source = sourceKind.ToSourceId(),
                displayName = sourceKind.ToDisplayName(),
                versions = versions.Select(ToDto)
            });
        });

        group.MapPost("/hosts/{hostId}/update", async (
            string hostId,
            QueueHostWorkerUpdateApiRequest body,
            HttpRequest request,
            IDocumentStore store,
            IConfiguration configuration,
            HostWorkerUpdateService updateService,
            CancellationToken ct) =>
        {
            if (!await IsAuthorizedAsync(request, store, configuration, ct))
                return Results.Unauthorized();

            if (!HostWorkerUpdateSourceKinds.TryParse(body.Source, out var sourceKind))
                return Results.BadRequest(new { error = $"Unsupported HostWorker update source '{body.Source}'." });

            var selection = new HostWorkerUpdateSelection(
                sourceKind,
                body.Version,
                body.Force,
                body.AllowNonUpgrade || sourceKind != HostWorkerUpdateSourceKind.Release,
                ResolvePublicBaseUrl(request, configuration));

            try
            {
                await updateService.QueueUpdateAsync(hostId, selection, ct);
                return Results.Accepted($"/hosts", new
                {
                    hostId,
                    source = sourceKind.ToSourceId(),
                    version = body.Version
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/artifacts/{source}/{version}/{assetName}", async (
            string source,
            string version,
            string assetName,
            string? sha256,
            HttpRequest request,
            HttpResponse response,
            IDocumentStore store,
            IConfiguration configuration,
            HostWorkerLocalUpdateStore localUpdateStore,
            CancellationToken ct) =>
        {
            if (!HostWorkerUpdateSourceKinds.TryParse(source, out var sourceKind) ||
                sourceKind == HostWorkerUpdateSourceKind.Release)
            {
                return Results.NotFound();
            }

            var artifact = await localUpdateStore.GetArtifactAsync(sourceKind, version, assetName, ct);
            if (artifact == null)
                return Results.NotFound();

            var authorization = await TryAuthorizeAsync(request, store, configuration, ct);
            if (authorization == false)
                return Results.Unauthorized();

            if (authorization != true && !HostEnrollmentToken.FixedTimeEquals(artifact.Sha256, sha256))
                return Results.Unauthorized();

            response.ContentType = artifact.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? "application/zip"
                : "application/gzip";
            response.Headers.ContentLength = artifact.SizeBytes;
            response.Headers.ContentDisposition = $"attachment; filename=\"{artifact.AssetName}\"";

            await localUpdateStore.WriteArtifactAsync(artifact, response.Body, ct);
            return Results.Empty;
        });

        group.MapGet("/github-artifacts/{runId:long}/{artifactName}/{assetName}", async (
            long runId,
            string artifactName,
            string assetName,
            string? sha256,
            HttpRequest request,
            HttpResponse response,
            IDocumentStore store,
            IConfiguration configuration,
            HostWorkerUpdateService updateService,
            CancellationToken ct) =>
        {
            var authorization = await TryAuthorizeAsync(request, store, configuration, ct);
            if (authorization == false)
                return Results.Unauthorized();

            if (authorization != true && string.IsNullOrWhiteSpace(sha256))
                return Results.Unauthorized();

            if (!HostWorkerUpdateSelector.IsHostWorkerAssetName(assetName))
                return Results.NotFound();

            var tempPath = Path.Combine(Path.GetTempPath(), $"runnerrunner-hostworker-update-{Guid.NewGuid():N}-{assetName}");
            try
            {
                await updateService.ExtractGitHubActionsArtifactAssetAsync(runId, artifactName, assetName, tempPath, ct);

                await using (var hashStream = File.OpenRead(tempPath))
                {
                    var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct)).ToLowerInvariant();
                    if (authorization != true && !HostEnrollmentToken.FixedTimeEquals(actualSha256, sha256))
                        return Results.Unauthorized();
                }

                var fileInfo = new FileInfo(tempPath);
                response.ContentType = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? "application/zip"
                    : "application/gzip";
                response.Headers.ContentLength = fileInfo.Length;
                response.Headers.ContentDisposition = $"attachment; filename=\"{assetName}\"";

                await using var input = File.OpenRead(tempPath);
                await input.CopyToAsync(response.Body, ct);
                return Results.Empty;
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        });

        return endpoints;
    }

    private static async Task<bool?> TryAuthorizeAsync(
        HttpRequest request,
        IDocumentStore store,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(GetEnrollmentToken(request)))
            return null;

        return await IsAuthorizedAsync(request, store, configuration, ct);
    }

    private static async Task<bool> IsAuthorizedAsync(
        HttpRequest request,
        IDocumentStore store,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var token = GetEnrollmentToken(request);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (HostEnrollmentToken.FixedTimeEquals(configuration["HostWorker:EnrollmentToken"], token))
            return true;

        var hosts = await store.Query<Host>().ToList();
        return hosts.Any(host => HostEnrollmentToken.Matches(host, token));
    }

    private static string? GetEnrollmentToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        var headerToken = request.Headers["X-RunnerRunner-Enrollment-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerToken))
            return headerToken.Trim();

        return null;
    }

    private static string ResolvePublicBaseUrl(HttpRequest request, IConfiguration configuration)
    {
        var configured = configuration["HostWorkerUpdates:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
    }

    private static object ToDto(HostWorkerUpdateVersion version)
        => new
        {
            source = version.Source.ToSourceId(),
            version = version.Version,
            createdAt = version.CreatedAt,
            assets = version.Assets.Select(ToDto)
        };

    private static object ToDto(HostWorkerUpdateArtifact artifact)
        => new
        {
            source = artifact.Source.ToSourceId(),
            version = artifact.Version,
            runtimeIdentifier = artifact.RuntimeIdentifier,
            assetName = artifact.AssetName,
            sha256 = artifact.Sha256,
            sizeBytes = artifact.SizeBytes,
            createdAt = artifact.CreatedAt
        };

    private sealed class QueueHostWorkerUpdateApiRequest
    {
        public string? Source { get; init; }
        public string? Version { get; init; }
        public bool Force { get; init; }
        public bool AllowNonUpgrade { get; init; }
    }
}
