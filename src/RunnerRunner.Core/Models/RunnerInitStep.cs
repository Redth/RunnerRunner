using Orleans;

namespace RunnerRunner.Core.Models;

/// <summary>
/// When an init step runs relative to the runner lifecycle.
/// </summary>
public enum InitStepPhase
{
    /// <summary>Run before the runner process/agent starts.</summary>
    PreRunner = 0,
    /// <summary>Run after the runner process/agent exits, before cleanup.</summary>
    PostExit = 1,
}

/// <summary>
/// Shell used to execute an init step.
/// </summary>
public enum InitStepShell
{
    /// <summary>Pick bash or sh on Unix, PowerShell on Windows.</summary>
    Auto = 0,
    Bash = 1,
    Sh = 2,
    PowerShell = 3,
    Cmd = 4,
}

/// <summary>
/// A user-defined script step executed as part of runner provisioning on the host.
/// Steps are attached to a <see cref="RunnerProfile"/> and run in list order per phase
/// for every runner instance started from that profile (across Docker, Tart and Native
/// backends).
/// </summary>
[GenerateSerializer]
public class RunnerInitStep
{
    [Id(0)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Id(1)]
    public required string Name { get; set; }
    [Id(2)]
    public InitStepPhase Phase { get; set; } = InitStepPhase.PreRunner;
    [Id(3)]
    public InitStepShell Shell { get; set; } = InitStepShell.Auto;
    [Id(4)]
    public string Script { get; set; } = "";
    [Id(5)]
    public bool ContinueOnError { get; set; }
    [Id(6)]
    public int TimeoutSeconds { get; set; } = 600;
    [Id(7)]
    public string? WorkingDirectory { get; set; }

    // Env composition (mirrors RunnerProfile env semantics)
    [Id(8)]
    public List<string> EnvironmentVariableSetIds { get; set; } = [];
    [Id(9)]
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    [Id(10)]
    public HashSet<string> EnvironmentOverrideSecretKeys { get; set; } = [];

    [Id(11)]
    public bool Enabled { get; set; } = true;
}
