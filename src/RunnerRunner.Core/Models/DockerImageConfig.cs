using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public class DockerImageConfig
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string RegistryUrl { get; set; }
    [Id(2)]
    public required string ImageName { get; set; }
    [Id(3)]
    public string Tag { get; set; } = "latest";
    [Id(4)]
    public PullPolicy PullPolicy { get; set; } = PullPolicy.IfNotPresent;
    [Id(5)]
    public string? CredentialId { get; set; }
}
