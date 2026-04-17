namespace RunnerRunner.Core.Models;

public enum RegistryType
{
    Docker,
    Tart
}

/// <summary>
/// Centralized container/OCI registry credential stored on the server.
/// Shared across agents — sent to agents on demand for authentication.
/// </summary>
public class RegistryCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public required string RegistryUrl { get; set; }
    public RegistryType RegistryType { get; set; } = RegistryType.Docker;
    public string? DefaultNamespace { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
