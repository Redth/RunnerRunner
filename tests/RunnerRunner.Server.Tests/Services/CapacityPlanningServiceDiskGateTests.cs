using Microsoft.Extensions.Configuration;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

/// <summary>
/// Covers the free-space gate that keeps jobs off hosts which would fail mid-build with
/// "No space left on device" (MacM2ini exhausted its disk twice on 2026-10-02).
/// </summary>
public class CapacityPlanningServiceDiskGateTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static long Gb(double value) => (long)(value * 1024 * 1024 * 1024);

    private static Host HostWithFreeDisk(double? freeGb, DateTime? observedAt, string name = "mac-a")
        => new()
        {
            Name = name,
            Platform = HostPlatform.MacOS,
            MaxNativeProcesses = 2,
            Capabilities = ["native"],
            AgentStatus = AgentStatus.Online,
            ObservedFreeDiskBytes = freeGb is double value ? Gb(value) : null,
            ObservedTotalDiskBytes = Gb(228),
            ObservedResourceUsageAt = observedAt,
            ObservedDiskUsageAt = observedAt
        };

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenFreeSpaceUnderFloor_BlocksHost()
    {
        var host = HostWithFreeDisk(12, Now.AddMinutes(-1));

        var blocked = CapacityPlanningService.IsBelowMinimumFreeDisk(host, 30, Now, out var detail);

        Assert.True(blocked);
        Assert.Contains("low on disk", detail);
        Assert.Contains("12.0 GB free", detail);
    }

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenFreeSpaceAboveFloor_AllowsHost()
    {
        var host = HostWithFreeDisk(125, Now.AddMinutes(-1));

        Assert.False(CapacityPlanningService.IsBelowMinimumFreeDisk(host, 30, Now, out _));
    }

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenHostNeverReportedDisk_FailsOpen()
    {
        // Hosts running an older HostWorker never send disk telemetry and must stay schedulable.
        var host = HostWithFreeDisk(null, Now.AddMinutes(-1));

        Assert.False(CapacityPlanningService.IsBelowMinimumFreeDisk(host, 30, Now, out _));
    }

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenReadingIsStale_FailsOpen()
    {
        // A reading taken before a prune ran must not keep the host blocked forever.
        var host = HostWithFreeDisk(1, Now - CapacityPlanningService.DiskObservationFreshness - TimeSpan.FromMinutes(1));

        Assert.False(CapacityPlanningService.IsBelowMinimumFreeDisk(host, 30, Now, out _));
    }

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenReadingHasNoTimestamp_FailsOpen()
    {
        var host = HostWithFreeDisk(1, observedAt: null);

        Assert.False(CapacityPlanningService.IsBelowMinimumFreeDisk(host, 30, Now, out _));
    }

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenThresholdDisabled_AllowsHost()
    {
        var host = HostWithFreeDisk(0.5, Now.AddMinutes(-1));

        Assert.False(CapacityPlanningService.IsBelowMinimumFreeDisk(host, 0, Now, out _));
    }

    private static (RunnerProfile Profile, Dictionary<string, RunnerProfile> ById) NativeProfile()
    {
        var profile = new RunnerProfile
        {
            Name = "mac-native",
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = ExecutionBackend.Native,
            MaxParallelPerHost = 2
        };

        return (profile, new Dictionary<string, RunnerProfile>(StringComparer.OrdinalIgnoreCase) { [profile.Id] = profile });
    }

    [Fact]
    public void AnalyzeHostSelection_SkipsLowDiskHostInFavourOfHealthyHost()
    {
        var (profile, profilesById) = NativeProfile();
        var lowDisk = HostWithFreeDisk(5, Now.AddMinutes(-1), "mac-low");
        var healthy = HostWithFreeDisk(400, Now.AddMinutes(-1), "mac-healthy");

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            rule: null,
            hosts: [lowDisk, healthy],
            profilesById,
            instances: [],
            requireDispatchReadiness: true,
            requestedRunnerLabels: null,
            minimumFreeDiskGb: 30,
            asOf: Now);

        Assert.NotNull(analysis.SelectedHost);
        Assert.Equal(healthy.Id, analysis.SelectedHost!.Id);
    }

    [Fact]
    public void AnalyzeHostSelection_WhenEveryHostIsLowOnDisk_ReportsCapacityBlocked()
    {
        var (profile, profilesById) = NativeProfile();
        var lowDisk = HostWithFreeDisk(5, Now.AddMinutes(-1), "mac-low");

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            rule: null,
            hosts: [lowDisk],
            profilesById,
            instances: [],
            requireDispatchReadiness: true,
            requestedRunnerLabels: null,
            minimumFreeDiskGb: 30,
            asOf: Now);

        Assert.Null(analysis.SelectedHost);
        // Capacity-blocked keeps the job queued and retrying instead of failing it outright.
        Assert.True(analysis.CapacityBlocked);
        Assert.Contains("low on disk", analysis.Reason);
    }

    [Fact]
    public void AnalyzeHostSelection_WithoutDiskThreshold_StillSelectsLowDiskHost()
    {
        var (profile, profilesById) = NativeProfile();
        var lowDisk = HostWithFreeDisk(5, Now.AddMinutes(-1), "mac-low");

        var analysis = CapacityPlanningService.AnalyzeHostSelection(
            profile,
            rule: null,
            hosts: [lowDisk],
            profilesById,
            instances: [],
            requireDispatchReadiness: true,
            requestedRunnerLabels: null,
            minimumFreeDiskGb: 0,
            asOf: Now);

        Assert.NotNull(analysis.SelectedHost);
    }

    [Fact]
    public void IsBelowMinimumFreeDisk_WhenOnlyTartUsageRefreshed_StillTreatsDiskAsStale()
    {
        // Regression: a heartbeat that carries Tart usage but no disk reading must not make an
        // old low-disk figure look fresh, or the host stays blocked forever once its disk probe
        // starts failing.
        var host = HostWithFreeDisk(12, Now.AddMinutes(-1));
        host.ObservedResourceUsageAt = Now;
        host.ObservedDiskUsageAt = Now.AddHours(-3);

        Assert.False(CapacityPlanningService.IsBelowMinimumFreeDisk(host, 30, Now, out _));
    }

    [Fact]
    public void ResolveMinimumFreeDiskGb_WithoutConfiguration_IsDisabled()
    {
        // The gate must stay off by default so upgrading cannot make a tight-but-working host
        // permanently unschedulable.
        Assert.Equal(0, CapacityPlanningService.ResolveMinimumFreeDiskGb(null));
        Assert.Equal(0, CapacityPlanningService.DefaultMinimumFreeDiskGb);
    }

    [Fact]
    public void ResolveMinimumFreeDiskGb_ReadsConfiguredValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DynamicProvisioning:MinimumFreeDiskGb"] = "30"
            })
            .Build();

        Assert.Equal(30, CapacityPlanningService.ResolveMinimumFreeDiskGb(configuration));
    }
}
