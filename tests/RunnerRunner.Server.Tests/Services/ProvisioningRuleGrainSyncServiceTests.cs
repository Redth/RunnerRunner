using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class ProvisioningRuleGrainSyncServiceTests
{
    [Fact]
    public async Task ConfigureRuleAsync_SendsStaticRuleConfigToMatchingGrain()
    {
        var (service, grainFactory) = CreateService();
        var grain = Substitute.For<IProvisioningRuleGrain>();
        grainFactory.GetGrain<IProvisioningRuleGrain>("rule-1", null).Returns(grain);

        await service.ConfigureRuleAsync(new ProvisioningRule
        {
            Id = "rule-1",
            Name = "Linux static",
            Description = "Keep one warm",
            ProfileId = "profile-1",
            Type = ProvisioningType.Static,
            Enabled = true,
            DesiredCount = 2,
            TargetGroupId = "linux-pool",
            RequiredHostLabels = new Dictionary<string, string>
            {
                ["arch"] = "arm64"
            }
        });

        await grain.Received(1).SetConfig(Arg.Is<ProvisioningRuleConfig>(config =>
            config.Name == "Linux static"
            && config.Description == "Keep one warm"
            && config.ProfileId == "profile-1"
            && config.Type == ProvisioningType.Static
            && config.Enabled
            && config.DesiredCount == 2
            && config.TargetGroupId == "linux-pool"
            && config.RequiredHostLabels["arch"] == "arm64"));
    }

    [Fact]
    public async Task DisableRuleAsync_DisablesMatchingGrain()
    {
        var (service, grainFactory) = CreateService();
        var grain = Substitute.For<IProvisioningRuleGrain>();
        grainFactory.GetGrain<IProvisioningRuleGrain>("rule-1", null).Returns(grain);

        await service.DisableRuleAsync("rule-1");

        await grain.Received(1).Disable();
    }

    [Fact]
    public async Task StartupSync_ConfiguresPersistedRules()
    {
        var store = TestDocumentStore.Create();
        var enabledGrain = Substitute.For<IProvisioningRuleGrain>();
        var disabledGrain = Substitute.For<IProvisioningRuleGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IProvisioningRuleGrain>("enabled-rule", null).Returns(enabledGrain);
        grainFactory.GetGrain<IProvisioningRuleGrain>("disabled-rule", null).Returns(disabledGrain);

        await store.Insert(new ProvisioningRule
        {
            Id = "enabled-rule",
            Name = "Enabled static",
            ProfileId = "profile-1",
            Type = ProvisioningType.Static,
            Enabled = true
        });
        await store.Insert(new ProvisioningRule
        {
            Id = "disabled-rule",
            Name = "Disabled static",
            ProfileId = "profile-2",
            Type = ProvisioningType.Static,
            Enabled = false
        });

        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(grainFactory)
            .AddSingleton(new ProvisioningRuleGrainSyncService(
                grainFactory,
                NullLogger<ProvisioningRuleGrainSyncService>.Instance))
            .BuildServiceProvider();

        var startupSync = new ProvisioningRuleGrainStartupSyncService(
            services,
            NullLogger<ProvisioningRuleGrainStartupSyncService>.Instance);

        await startupSync.StartAsync(CancellationToken.None);

        await enabledGrain.Received(1).SetConfig(Arg.Is<ProvisioningRuleConfig>(config =>
            config.Name == "Enabled static"
            && config.Enabled));
        await disabledGrain.Received(1).SetConfig(Arg.Is<ProvisioningRuleConfig>(config =>
            config.Name == "Disabled static"
            && !config.Enabled));
    }

    private static (ProvisioningRuleGrainSyncService Service, IGrainFactory GrainFactory) CreateService()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        return (new ProvisioningRuleGrainSyncService(
            grainFactory,
            NullLogger<ProvisioningRuleGrainSyncService>.Instance), grainFactory);
    }
}
