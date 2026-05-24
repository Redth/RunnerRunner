using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Models;
using RunnerRunner.Server.Services.Auth;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

public sealed class SettingsBackupService
{
    private const int CurrentBackupVersion = 1;
    private const int EncryptionVersion = 1;
    private const int SaltSizeBytes = 16;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeySizeBytes = 32;
    private const int Pbkdf2Iterations = 210_000;
    private const string EncryptionFormat = "RunnerRunner.EncryptedSettingsBackup";
    private static readonly byte[] EncryptionAssociatedData = Encoding.UTF8.GetBytes("RunnerRunner.SettingsBackup.v1");

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IDocumentStore _store;
    private readonly RunnerRunnerAuthSettingsService? _authSettingsService;
    private readonly ProvisioningRuleGrainSyncService? _ruleSyncService;
    private readonly ILogger<SettingsBackupService> _logger;

    public SettingsBackupService(
        IDocumentStore store,
        ILogger<SettingsBackupService> logger,
        RunnerRunnerAuthSettingsService? authSettingsService = null,
        ProvisioningRuleGrainSyncService? ruleSyncService = null)
    {
        _store = store;
        _logger = logger;
        _authSettingsService = authSettingsService;
        _ruleSyncService = ruleSyncService;
    }

    public async Task<SettingsBackupFile> ExportProvisioningRulesJsonAsync(CancellationToken cancellationToken = default)
    {
        var rules = (await QueryDocumentsAsync<ProvisioningRule>()).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rules, JsonOptions);
        return new SettingsBackupFile(
            BuildFileName("runnerrunner-provisioning-rules", "json"),
            "application/json",
            bytes);
    }

    public async Task<SettingsBackupFile> ExportBackupAsync(string? password, CancellationToken cancellationToken = default)
    {
        var backup = await BuildBackupAsync();
        var zipBytes = CreateZip(backup);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        if (string.IsNullOrWhiteSpace(password))
        {
            return new SettingsBackupFile(
                $"runnerrunner-settings-backup-{timestamp}.zip",
                "application/zip",
                zipBytes);
        }

        var encrypted = Encrypt(zipBytes, password);
        return new SettingsBackupFile(
            $"runnerrunner-settings-backup-{timestamp}.rrbackup",
            "application/octet-stream",
            encrypted);
    }

    public async Task<SettingsBackupImportResult> ImportBackupAsync(Stream stream, string? password, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var zipBytes = IsZip(bytes) ? bytes : Decrypt(bytes, password);
        var backup = ReadZip(zipBytes);

        if (backup.Version > CurrentBackupVersion)
        {
            throw new InvalidOperationException(
                $"Backup version {backup.Version} is newer than this RunnerRunner server supports.");
        }

        var imported = new SettingsBackupImportResult();

        imported.ProviderCredentials = await UpsertAllAsync(backup.ProviderCredentials, x => x.Id, cancellationToken);
        imported.RegistryCredentials = await UpsertAllAsync(backup.RegistryCredentials, x => x.Id, cancellationToken);
        imported.EnvironmentVariableSets = await UpsertAllAsync(backup.EnvironmentVariableSets, x => x.Id, cancellationToken);
        imported.CustomSteps = await UpsertAllAsync(backup.CustomSteps, x => x.Id, cancellationToken);
        imported.RunnerProfiles = await UpsertAllAsync(backup.RunnerProfiles, x => x.Id, cancellationToken);

        if (_authSettingsService != null && backup.AuthSettings != null)
        {
            var settings = backup.AuthSettings.Settings;
            settings.Oidc.ProtectedClientSecret = null;

            await _authSettingsService.SaveAsync(settings, backup.AuthSettings.OidcClientSecret);
            imported.AuthSettings = 1;
        }

        imported.ProvisioningRules = await UpsertAllAsync(backup.ProvisioningRules, x => x.Id, cancellationToken);
        if (_ruleSyncService != null)
        {
            foreach (var rule in backup.ProvisioningRules)
                await _ruleSyncService.ConfigureRuleAsync(rule);
        }

        imported.HostHints = backup.HostHints.Count;
        imported.Warnings.AddRange(BuildImportWarnings(backup));

        _logger.LogInformation(
            "Imported RunnerRunner settings backup: {ProvisioningRules} rules, {ProviderCredentials} provider credentials, {RegistryCredentials} registries, {EnvironmentVariableSets} env var sets, {CustomSteps} custom steps, {RunnerProfiles} profiles",
            imported.ProvisioningRules,
            imported.ProviderCredentials,
            imported.RegistryCredentials,
            imported.EnvironmentVariableSets,
            imported.CustomSteps,
            imported.RunnerProfiles);

        return imported;
    }

    private async Task<RunnerRunnerSettingsBackup> BuildBackupAsync()
    {
        var authSettings = _authSettingsService == null
            ? null
            : new SettingsBackupAuthSettings
            {
                Settings = await _authSettingsService.GetAsync(),
                OidcClientSecret = _authSettingsService.GetOidcClientSecret()
            };

        var hosts = await QueryDocumentsAsync<Host>();
        var rules = (await QueryDocumentsAsync<ProvisioningRule>()).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var backup = new RunnerRunnerSettingsBackup
        {
            Version = CurrentBackupVersion,
            ExportedAtUtc = DateTime.UtcNow,
            ProvisioningRules = rules,
            RunnerProfiles = (await QueryDocumentsAsync<RunnerProfile>()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ProviderCredentials = (await QueryDocumentsAsync<ProviderCredential>()).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            RegistryCredentials = (await QueryDocumentsAsync<RegistryCredential>()).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            EnvironmentVariableSets = (await QueryDocumentsAsync<EnvironmentVariableSet>()).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            CustomSteps = (await QueryDocumentsAsync<RunnerInitStepDefinition>()).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            AuthSettings = authSettings,
            HostHints = BuildHostHints(hosts)
        };

        backup.Manifest = SettingsBackupManifest.FromBackup(backup);
        return backup;
    }

    private async Task<List<T>> QueryDocumentsAsync<T>() where T : class
    {
        try
        {
            return (await _store.Query<T>().ToList()).ToList();
        }
        catch (Exception ex) when (IsMissingDocumentTable(ex))
        {
            _logger.LogDebug(ex, "Document table for {DocumentType} does not exist yet; exporting an empty collection.", typeof(T).Name);
            return [];
        }
    }

    private static List<SettingsBackupHostHint> BuildHostHints(IReadOnlyCollection<Host> hosts)
    {
        return [.. hosts
            .OrderBy(host => host.Label, StringComparer.OrdinalIgnoreCase)
            .Select(host => new SettingsBackupHostHint
            {
                Id = host.Id,
                Name = host.Name,
                DisplayName = host.DisplayName,
                Platform = host.Platform,
                Architecture = host.Architecture,
                GroupId = host.GroupId,
                Capabilities = [.. host.Capabilities],
                Labels = new Dictionary<string, string>(host.Labels)
            })];
    }

    private static byte[] CreateZip(RunnerRunnerSettingsBackup backup)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddJsonEntry(archive, "manifest.json", backup.Manifest);
            AddJsonEntry(archive, "provisioning-rules.json", backup.ProvisioningRules);
            AddJsonEntry(archive, "runner-profiles.json", backup.RunnerProfiles);
            AddJsonEntry(archive, "provider-credentials.json", backup.ProviderCredentials);
            AddJsonEntry(archive, "registry-credentials.json", backup.RegistryCredentials);
            AddJsonEntry(archive, "env-var-sets.json", backup.EnvironmentVariableSets);
            AddJsonEntry(archive, "custom-steps.json", backup.CustomSteps);
            AddJsonEntry(archive, "auth-settings.json", backup.AuthSettings);
            AddJsonEntry(archive, "host-hints.json", backup.HostHints);
        }

        return buffer.ToArray();
    }

    private static RunnerRunnerSettingsBackup ReadZip(byte[] zipBytes)
    {
        using var buffer = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var backup = new RunnerRunnerSettingsBackup
        {
            Manifest = ReadJsonEntry<SettingsBackupManifest>(archive, "manifest.json") ?? new SettingsBackupManifest(),
            ProvisioningRules = ReadJsonEntry<List<ProvisioningRule>>(archive, "provisioning-rules.json") ?? [],
            RunnerProfiles = ReadJsonEntry<List<RunnerProfile>>(archive, "runner-profiles.json") ?? [],
            ProviderCredentials = ReadJsonEntry<List<ProviderCredential>>(archive, "provider-credentials.json") ?? [],
            RegistryCredentials = ReadJsonEntry<List<RegistryCredential>>(archive, "registry-credentials.json") ?? [],
            EnvironmentVariableSets = ReadJsonEntry<List<EnvironmentVariableSet>>(archive, "env-var-sets.json") ?? [],
            CustomSteps = ReadJsonEntry<List<RunnerInitStepDefinition>>(archive, "custom-steps.json") ?? [],
            AuthSettings = ReadJsonEntry<SettingsBackupAuthSettings>(archive, "auth-settings.json"),
            HostHints = ReadJsonEntry<List<SettingsBackupHostHint>>(archive, "host-hints.json") ?? []
        };
        backup.Version = backup.Manifest.Version == 0 ? CurrentBackupVersion : backup.Manifest.Version;
        backup.ExportedAtUtc = backup.Manifest.ExportedAtUtc;
        return backup;
    }

    private async Task<int> UpsertAllAsync<T>(
        IReadOnlyCollection<T> items,
        Func<T, string> idSelector,
        CancellationToken cancellationToken) where T : class
    {
        var count = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertAsync(item, idSelector(item));
            count++;
        }

        return count;
    }

    private async Task UpsertAsync<T>(T item, string id) where T : class
    {
        var existing = await TryGetAsync<T>(id);
        if (existing == null)
            await _store.Insert(item);
        else
            await _store.Update(item);
    }

    private async Task<T?> TryGetAsync<T>(string id) where T : class
    {
        try
        {
            return await _store.Get<T>(id);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMissingDocumentTable(Exception ex) =>
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("relation", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildImportWarnings(RunnerRunnerSettingsBackup backup)
    {
        var hintIds = backup.HostHints.Select(h => h.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = backup.ProvisioningRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.TargetHostId) && !hintIds.Contains(rule.TargetHostId))
            .Select(rule => $"Provisioning rule '{rule.Name}' targets host id '{rule.TargetHostId}', but that host was not included as a backup hint.")
            .ToList();

        return warnings;
    }

    private static byte[] Encrypt(byte[] zipBytes, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);

        var ciphertext = new byte[zipBytes.Length];
        var tag = new byte[TagSizeBytes];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, zipBytes, ciphertext, tag, EncryptionAssociatedData);

        CryptographicOperations.ZeroMemory(key);

        return JsonSerializer.SerializeToUtf8Bytes(new SettingsBackupEncryptedEnvelope
        {
            Format = EncryptionFormat,
            Version = EncryptionVersion,
            Kdf = "PBKDF2-HMAC-SHA256",
            Iterations = Pbkdf2Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(ciphertext)
        }, JsonOptions);
    }

    private static byte[] Decrypt(byte[] encryptedBytes, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("This backup is encrypted. Enter the export password to import it.");

        var envelope = JsonSerializer.Deserialize<SettingsBackupEncryptedEnvelope>(encryptedBytes, JsonOptions)
            ?? throw new InvalidOperationException("The selected file is not a valid RunnerRunner backup.");

        if (!string.Equals(envelope.Format, EncryptionFormat, StringComparison.Ordinal)
            || envelope.Version != EncryptionVersion)
        {
            throw new InvalidOperationException("The selected file is not a supported encrypted RunnerRunner backup.");
        }

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var plaintext = new byte[ciphertext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            envelope.Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);

        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, EncryptionAssociatedData);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Unable to decrypt the backup. Check the password and try again.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4
        && bytes[0] == 0x50
        && bytes[1] == 0x4b;

    private static void AddJsonEntry<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static T? ReadJsonEntry<T>(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null)
            return default;

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static string BuildFileName(string prefix, string extension) =>
        $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}";

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record SettingsBackupFile(string FileName, string ContentType, byte[] Bytes);

public sealed class SettingsBackupImportResult
{
    public int ProvisioningRules { get; set; }
    public int RunnerProfiles { get; set; }
    public int ProviderCredentials { get; set; }
    public int RegistryCredentials { get; set; }
    public int EnvironmentVariableSets { get; set; }
    public int CustomSteps { get; set; }
    public int AuthSettings { get; set; }
    public int HostHints { get; set; }
    public List<string> Warnings { get; } = [];

    public int TotalImported =>
        ProvisioningRules
        + RunnerProfiles
        + ProviderCredentials
        + RegistryCredentials
        + EnvironmentVariableSets
        + CustomSteps
        + AuthSettings;
}

public sealed class RunnerRunnerSettingsBackup
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public SettingsBackupManifest Manifest { get; set; } = new();
    public List<ProvisioningRule> ProvisioningRules { get; set; } = [];
    public List<RunnerProfile> RunnerProfiles { get; set; } = [];
    public List<ProviderCredential> ProviderCredentials { get; set; } = [];
    public List<RegistryCredential> RegistryCredentials { get; set; } = [];
    public List<EnvironmentVariableSet> EnvironmentVariableSets { get; set; } = [];
    public List<RunnerInitStepDefinition> CustomSteps { get; set; } = [];
    public SettingsBackupAuthSettings? AuthSettings { get; set; }
    public List<SettingsBackupHostHint> HostHints { get; set; } = [];
}

public sealed class SettingsBackupManifest
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SettingsBackupManifest FromBackup(RunnerRunnerSettingsBackup backup) => new()
    {
        Version = backup.Version,
        ExportedAtUtc = backup.ExportedAtUtc,
        Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["provisioningRules"] = backup.ProvisioningRules.Count,
            ["runnerProfiles"] = backup.RunnerProfiles.Count,
            ["providerCredentials"] = backup.ProviderCredentials.Count,
            ["registryCredentials"] = backup.RegistryCredentials.Count,
            ["environmentVariableSets"] = backup.EnvironmentVariableSets.Count,
            ["customSteps"] = backup.CustomSteps.Count,
            ["authSettings"] = backup.AuthSettings == null ? 0 : 1,
            ["hostHints"] = backup.HostHints.Count
        }
    };
}

public sealed class SettingsBackupAuthSettings
{
    public RunnerRunnerAuthSettings Settings { get; set; } = new();
    public string? OidcClientSecret { get; set; }
}

public sealed class SettingsBackupHostHint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public HostPlatform Platform { get; set; }
    public string? Architecture { get; set; }
    public string? GroupId { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = new();
}

public sealed class SettingsBackupEncryptedEnvelope
{
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public string Kdf { get; set; } = "";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Ciphertext { get; set; } = "";
}
