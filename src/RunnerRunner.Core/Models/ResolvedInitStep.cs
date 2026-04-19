using Orleans;

namespace RunnerRunner.Core.Models;

/// <summary>
/// Transport DTO representing a <see cref="RunnerInitStep"/> after the server has
/// resolved its env composition (sets + overrides) and collapsed Auto shell selection
/// based on the target backend/platform. Delivered to the agent as part of the deploy
/// command and executed verbatim.
/// </summary>
[GenerateSerializer]
public class ResolvedInitStep
{
    [Id(0)]
    public string Id { get; set; } = "";
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public InitStepPhase Phase { get; set; }
    /// <summary>
    /// Concrete shell to use on the agent. Never <see cref="InitStepShell.Auto"/> —
    /// the server resolves Auto before sending.
    /// </summary>
    [Id(3)]
    public InitStepShell Shell { get; set; }
    [Id(4)]
    public string Script { get; set; } = "";
    [Id(5)]
    public bool ContinueOnError { get; set; }
    [Id(6)]
    public int TimeoutSeconds { get; set; } = 600;
    [Id(7)]
    public string? WorkingDirectory { get; set; }

    /// <summary>Fully-composed env for this step (profile + step-level).</summary>
    [Id(8)]
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>Keys considered secret (for log masking on the agent).</summary>
    [Id(9)]
    public HashSet<string> SecretKeys { get; set; } = [];
}
