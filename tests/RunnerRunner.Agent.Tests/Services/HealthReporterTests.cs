using Microsoft.Extensions.Logging;
using NSubstitute;
using RunnerRunner.Agent.Services;

namespace RunnerRunner.Agent.Tests.Services;

public class HealthReporterTests
{
    [Fact]
    public void CollectMetrics_ReturnsCorrectAgentId()
    {
        var logger = Substitute.For<ILogger<HealthReporter>>();
        var lifecycleLogger = Substitute.For<ILogger<RunnerLifecycleManager>>();
        var lifecycle = new RunnerLifecycleManager(lifecycleLogger);
        var reporter = new HealthReporter(logger, lifecycle);

        var metrics = reporter.CollectMetrics("agent-42");

        Assert.Equal("agent-42", metrics.AgentId);
    }

    [Fact]
    public void CollectMetrics_ReportsZeroRunners_WhenNoneRunning()
    {
        var logger = Substitute.For<ILogger<HealthReporter>>();
        var lifecycleLogger = Substitute.For<ILogger<RunnerLifecycleManager>>();
        var lifecycle = new RunnerLifecycleManager(lifecycleLogger);
        var reporter = new HealthReporter(logger, lifecycle);

        var metrics = reporter.CollectMetrics("agent-1");

        Assert.Equal(0, metrics.RunningInstanceCount);
    }

    [Fact]
    public void CollectMetrics_ReportsMemoryUsage()
    {
        var logger = Substitute.For<ILogger<HealthReporter>>();
        var lifecycleLogger = Substitute.For<ILogger<RunnerLifecycleManager>>();
        var lifecycle = new RunnerLifecycleManager(lifecycleLogger);
        var reporter = new HealthReporter(logger, lifecycle);

        var metrics = reporter.CollectMetrics("agent-1");

        // Should be a reasonable percentage (not negative, not over 100)
        Assert.True(metrics.MemoryUsagePercent >= 0);
        Assert.True(metrics.MemoryUsagePercent <= 100);
    }
}
