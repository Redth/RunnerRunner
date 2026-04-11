namespace RunnerRunner.Core.Models;

public class DockerImageConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string RegistryUrl { get; set; }
    public required string ImageName { get; set; }
    public string Tag { get; set; } = "latest";
    public PullPolicy PullPolicy { get; set; } = PullPolicy.IfNotPresent;
    public string? CredentialId { get; set; }
}
