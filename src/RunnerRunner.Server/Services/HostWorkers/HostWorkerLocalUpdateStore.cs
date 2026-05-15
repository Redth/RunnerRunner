using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RunnerRunner.Server.Services.HostWorkers;

public enum HostWorkerUpdateSourceKind
{
    Release,
    Upload,
    LocalFolder
}

public static class HostWorkerUpdateSourceKinds
{
    public static string ToSourceId(this HostWorkerUpdateSourceKind source)
        => source switch
        {
            HostWorkerUpdateSourceKind.Release => "release",
            HostWorkerUpdateSourceKind.Upload => "upload",
            HostWorkerUpdateSourceKind.LocalFolder => "local-folder",
            _ => source.ToString()
        };

    public static string ToDisplayName(this HostWorkerUpdateSourceKind source)
        => source switch
        {
            HostWorkerUpdateSourceKind.Release => "GitHub ref",
            HostWorkerUpdateSourceKind.Upload => "Uploaded builds",
            HostWorkerUpdateSourceKind.LocalFolder => "Local folder",
            _ => source.ToString()
        };

    public static bool TryParse(string? value, out HostWorkerUpdateSourceKind source)
    {
        source = HostWorkerUpdateSourceKind.Release;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        source = normalized switch
        {
            "release" or "releases" or "github" or "github-releases" => HostWorkerUpdateSourceKind.Release,
            "upload" or "uploads" or "uploaded" or "uploaded-builds" => HostWorkerUpdateSourceKind.Upload,
            "local" or "local-folder" or "folder" or "ssh" or "ssh-local-folder" => HostWorkerUpdateSourceKind.LocalFolder,
            _ => HostWorkerUpdateSourceKind.Release
        };
        return normalized is "release" or "releases" or "github" or "github-releases"
            or "upload" or "uploads" or "uploaded" or "uploaded-builds"
            or "local" or "local-folder" or "folder" or "ssh" or "ssh-local-folder";
    }
}

public sealed record HostWorkerUpdateArtifact(
    HostWorkerUpdateSourceKind Source,
    string Version,
    string RuntimeIdentifier,
    string AssetName,
    string Sha256,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record HostWorkerUpdateVersion(
    HostWorkerUpdateSourceKind Source,
    string Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<HostWorkerUpdateArtifact> Assets);

public sealed class HostWorkerLocalUpdateStore
{
    private static readonly Regex AssetNamePattern = new(
        @"^runnerrunner-hostworker-(?<rid>linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64)\.(?<extension>tar\.gz|zip)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<HostWorkerLocalUpdateStore> _logger;

    public HostWorkerLocalUpdateStore(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<HostWorkerLocalUpdateStore> logger)
    {
        _logger = logger;
        var storageRoot = configuration["HostWorkerUpdates:StorageRoot"];
        if (string.IsNullOrWhiteSpace(storageRoot))
            storageRoot = Path.Combine(environment.ContentRootPath, "data", "hostworker-updates");

        var uploadRoot = configuration["HostWorkerUpdates:UploadRoot"];
        if (string.IsNullOrWhiteSpace(uploadRoot))
            uploadRoot = Path.Combine(storageRoot, "uploads");

        var localFolderRoot = configuration["HostWorkerUpdates:LocalArtifactRoot"];
        if (string.IsNullOrWhiteSpace(localFolderRoot))
            localFolderRoot = Path.Combine(storageRoot, "local");

        UploadRoot = uploadRoot;
        LocalFolderRoot = localFolderRoot;
        MaxUploadBytes = configuration.GetValue("HostWorkerUpdates:MaxUploadBytes", 500L * 1024L * 1024L);

        Directory.CreateDirectory(UploadRoot);
        Directory.CreateDirectory(LocalFolderRoot);
    }

    public string UploadRoot { get; }
    public string LocalFolderRoot { get; }
    public long MaxUploadBytes { get; }

    public IReadOnlyList<HostWorkerUpdateVersion> ListVersions(HostWorkerUpdateSourceKind source)
    {
        if (source == HostWorkerUpdateSourceKind.Release)
            return [];

        var artifacts = EnumerateArtifacts(source).ToList();
        return artifacts
            .GroupBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HostWorkerUpdateVersion(
                source,
                group.Key,
                group.Max(x => x.CreatedAt),
                group.OrderBy(x => x.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public HostWorkerUpdateVersion? GetVersion(HostWorkerUpdateSourceKind source, string? version)
    {
        var versions = ListVersions(source);
        if (string.IsNullOrWhiteSpace(version))
            return versions.FirstOrDefault();

        return versions.FirstOrDefault(x => string.Equals(x.Version, version.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<HostWorkerUpdateArtifact> SaveUploadAsync(
        string version,
        string assetName,
        Stream content,
        long sizeBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("A HostWorker update version is required.");

        if (sizeBytes <= 0)
            throw new InvalidOperationException("The uploaded HostWorker update artifact is empty.");

        if (sizeBytes > MaxUploadBytes)
            throw new InvalidOperationException($"The uploaded HostWorker update artifact exceeds the {MaxUploadBytes:n0} byte limit.");

        var safeVersion = SanitizeVersion(version);
        if (!string.Equals(version.Trim(), safeVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("HostWorker update versions can only contain letters, numbers, '.', '-', and '_'.");

        if (!TryParseAssetName(assetName, out var runtimeIdentifier, out var normalizedAssetName))
            throw new InvalidOperationException($"Unsupported HostWorker update artifact '{assetName}'. Expected runnerrunner-hostworker-<rid>.tar.gz or .zip.");

        var targetDirectory = Path.Combine(UploadRoot, safeVersion);
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(targetDirectory, normalizedAssetName);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(output, ct);

            var actualSize = new FileInfo(tempPath).Length;
            if (actualSize <= 0)
                throw new InvalidOperationException("The uploaded HostWorker update artifact is empty.");
            if (actualSize > MaxUploadBytes)
                throw new InvalidOperationException($"The uploaded HostWorker update artifact exceeds the {MaxUploadBytes:n0} byte limit.");

            File.Move(tempPath, targetPath, overwrite: true);
            var artifact = await CreateArtifactAsync(HostWorkerUpdateSourceKind.Upload, safeVersion, runtimeIdentifier, normalizedAssetName, targetPath, ct);
            _logger.LogInformation("Stored uploaded HostWorker artifact {AssetName} for version {Version}", artifact.AssetName, artifact.Version);
            return artifact;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public async Task<HostWorkerUpdateArtifact?> GetArtifactAsync(
        HostWorkerUpdateSourceKind source,
        string version,
        string assetName,
        CancellationToken ct = default)
    {
        if (source == HostWorkerUpdateSourceKind.Release)
            return null;

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(assetName))
            return null;

        if (!TryParseAssetName(assetName, out var runtimeIdentifier, out var normalizedAssetName))
            return null;

        var artifactPath = ResolveArtifactPath(source, SanitizeVersion(version), normalizedAssetName);
        if (artifactPath == null || !File.Exists(artifactPath))
            return null;

        return await CreateArtifactAsync(source, SanitizeVersion(version), runtimeIdentifier, normalizedAssetName, artifactPath, ct);
    }

    public async Task WriteArtifactAsync(
        HostWorkerUpdateArtifact artifact,
        Stream output,
        CancellationToken ct = default)
    {
        var artifactPath = ResolveArtifactPath(artifact.Source, artifact.Version, artifact.AssetName)
            ?? throw new FileNotFoundException($"HostWorker update artifact '{artifact.AssetName}' was not found.");
        await using var input = File.OpenRead(artifactPath);
        await input.CopyToAsync(output, ct);
    }

    public string BuildDownloadUrl(HostWorkerUpdateArtifact artifact, string publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            throw new InvalidOperationException("HostWorkerUpdates:PublicBaseUrl or a request base URL is required for local HostWorker update artifacts.");

        var baseUri = publicBaseUrl.EndsWith("/", StringComparison.Ordinal) ? publicBaseUrl : publicBaseUrl + "/";
        var relative = $"api/hostworker-updates/artifacts/{Uri.EscapeDataString(artifact.Source.ToSourceId())}/{Uri.EscapeDataString(artifact.Version)}/{Uri.EscapeDataString(artifact.AssetName)}?sha256={Uri.EscapeDataString(artifact.Sha256)}";
        return new Uri(new Uri(baseUri), relative).ToString();
    }

    private IEnumerable<HostWorkerUpdateArtifact> EnumerateArtifacts(HostWorkerUpdateSourceKind source)
    {
        var root = source switch
        {
            HostWorkerUpdateSourceKind.Upload => UploadRoot,
            HostWorkerUpdateSourceKind.LocalFolder => LocalFolderRoot,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (!TryParseAssetName(Path.GetFileName(file), out var runtimeIdentifier, out var normalizedAssetName))
                continue;

            yield return CreateArtifact(source, "local", runtimeIdentifier, normalizedAssetName, file);
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var version = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(version))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!TryParseAssetName(Path.GetFileName(file), out var runtimeIdentifier, out var normalizedAssetName))
                    continue;

                yield return CreateArtifact(source, version, runtimeIdentifier, normalizedAssetName, file);
            }
        }
    }

    private string? ResolveArtifactPath(HostWorkerUpdateSourceKind source, string version, string assetName)
    {
        var root = source switch
        {
            HostWorkerUpdateSourceKind.Upload => UploadRoot,
            HostWorkerUpdateSourceKind.LocalFolder => LocalFolderRoot,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(root))
            return null;

        if (source == HostWorkerUpdateSourceKind.LocalFolder && string.Equals(version, "local", StringComparison.OrdinalIgnoreCase))
        {
            var rootArtifact = Path.Combine(root, assetName);
            if (IsSafeChildPath(root, rootArtifact))
                return rootArtifact;
        }

        var versionPath = Path.Combine(root, version, assetName);
        return IsSafeChildPath(root, versionPath) ? versionPath : null;
    }

    private static bool IsSafeChildPath(string root, string path)
    {
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pathFullPath = Path.GetFullPath(path);
        return pathFullPath.StartsWith(rootFullPath, StringComparison.Ordinal);
    }

    private static async Task<HostWorkerUpdateArtifact> CreateArtifactAsync(
        HostWorkerUpdateSourceKind source,
        string version,
        string runtimeIdentifier,
        string assetName,
        string path,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var info = new FileInfo(path);
        return new HostWorkerUpdateArtifact(source, version, runtimeIdentifier, assetName, sha256, info.Length, info.LastWriteTimeUtc);
    }

    private static HostWorkerUpdateArtifact CreateArtifact(
        HostWorkerUpdateSourceKind source,
        string version,
        string runtimeIdentifier,
        string assetName,
        string path)
    {
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var info = new FileInfo(path);
        return new HostWorkerUpdateArtifact(source, version, runtimeIdentifier, assetName, sha256, info.Length, info.LastWriteTimeUtc);
    }

    private static bool TryParseAssetName(string assetName, out string runtimeIdentifier, out string normalizedAssetName)
    {
        runtimeIdentifier = "";
        normalizedAssetName = Path.GetFileName(assetName);
        if (!string.Equals(assetName, normalizedAssetName, StringComparison.Ordinal))
            return false;

        var match = AssetNamePattern.Match(normalizedAssetName);
        if (!match.Success)
            return false;

        runtimeIdentifier = match.Groups["rid"].Value.ToLowerInvariant();
        normalizedAssetName = $"runnerrunner-hostworker-{runtimeIdentifier}.{match.Groups["extension"].Value.ToLowerInvariant()}";

        if (runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
            return normalizedAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        return normalizedAssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeVersion(string value)
    {
        var builder = new StringBuilder(value.Trim().Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
                builder.Append(ch);
            else
                builder.Append('_');
        }

        return builder.Length == 0 ? "local" : builder.ToString();
    }
}
