using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Tests;

public class RunnerInitStepTests
{
    [Fact]
    public void RunnerInitStep_Defaults()
    {
        var step = new RunnerInitStep { Name = "install" };
        Assert.False(string.IsNullOrEmpty(step.Id));
        Assert.True(Guid.TryParse(step.Id, out _));
        Assert.Equal(InitStepPhase.PreRunner, step.Phase);
        Assert.Equal(InitStepShell.Auto, step.Shell);
        Assert.Equal("", step.Script);
        Assert.False(step.ContinueOnError);
        Assert.Equal(600, step.TimeoutSeconds);
        Assert.Null(step.WorkingDirectory);
        Assert.Empty(step.EnvironmentVariableSetIds);
        Assert.Empty(step.EnvironmentOverrides);
        Assert.Empty(step.EnvironmentOverrideSecretKeys);
        Assert.True(step.Enabled);
    }

    [Fact]
    public void RunnerProfile_InitSteps_DefaultsEmpty()
    {
        var profile = new RunnerProfile { Name = "p" };
        Assert.NotNull(profile.InitSteps);
        Assert.Empty(profile.InitSteps);
    }

    [Fact]
    public void ResolvedInitStep_Defaults()
    {
        var r = new ResolvedInitStep { Name = "x" };
        Assert.Equal("", r.Id);
        Assert.Equal(InitStepPhase.PreRunner, r.Phase);
        Assert.Equal(600, r.TimeoutSeconds);
        Assert.Empty(r.EnvironmentVariables);
        Assert.Empty(r.SecretKeys);
    }
}
