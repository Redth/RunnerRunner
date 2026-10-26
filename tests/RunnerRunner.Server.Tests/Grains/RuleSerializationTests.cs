using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Storage;
using RunnerRunner.Server.Data;
using Newtonsoft.Json.Linq;
using RunnerRunner.Server.Grains.State;

namespace RunnerRunner.Server.Tests.Grains;

public class RuleSerializationTests
{
    [Fact]
    public void StorageRetainsDefaultValuesAndExistingCreationDefaults()
    {
        var services = new ServiceCollection();
        services.PostConfigure<OrleansJsonSerializerOptions>(GrainStorageSerialization.Configure);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OrleansJsonSerializerOptions>>();
        var storage = new JsonGrainStorageSerializer(new OrleansJsonSerializer(options));
        var payload = JObject.Parse(storage.Serialize(new HostGrainState()).ToString());
        Assert.Equal(false, payload["IsDraining"]?.Value<bool>());
        var created = storage.Deserialize<ProvisioningRuleGrainState>(storage.Serialize(new ProvisioningRuleGrainState()));
        Assert.True(created.Config.Enabled);
        Assert.Equal(1, created.Config.DesiredCount);
        Assert.Equal(5, created.Config.MaxInstances);
    }

    [Fact]
    public void DisabledRuleAndZeroLimitsSurviveProductionJsonStorage()
    {
        var options = new OrleansJsonSerializerOptions();
        GrainStorageSerialization.Configure(options);
        var storage = new JsonGrainStorageSerializer(new OrleansJsonSerializer(Options.Create(options)));
        var rule = new ProvisioningRuleGrainState
        { Config = new ProvisioningRuleConfig { Enabled = false, DesiredCount = 0, MaxInstances = 0, CooldownSeconds = 0, ScaleDownDelaySeconds = 0 } };
        var copy = storage.Deserialize<ProvisioningRuleGrainState>(storage.Serialize(rule));
        Assert.False(copy.Config.Enabled);
        Assert.Equal(0, copy.Config.DesiredCount);
        Assert.Equal(0, copy.Config.MaxInstances);
        Assert.Equal(0, copy.Config.CooldownSeconds);
        Assert.Equal(0, copy.Config.ScaleDownDelaySeconds);
        var host = new HostGrainState { MaxDockerContainers = 0, MaxNativeProcesses = 0, IsDraining = true };
        var restored = storage.Deserialize<HostGrainState>(storage.Serialize(host));
        Assert.Equal(0, restored.MaxDockerContainers);
        Assert.Equal(0, restored.MaxNativeProcesses);
        Assert.True(restored.IsDraining);
    }

    [Fact]
    public void LegacyMissingFlagsAndLimitsLoadAsDisabledAndZero()
    {
        var options = new OrleansJsonSerializerOptions();
        GrainStorageSerialization.Configure(options);
        var storage = new JsonGrainStorageSerializer(new OrleansJsonSerializer(Options.Create(options)));
        // The old production serializer omitted each false/zero property entirely.
        var rule = storage.Deserialize<ProvisioningRuleGrainState>(new BinaryData("""
            {"Config":{"Name":"disabled","ProfileId":"profile","Type":0}}
            """));
        Assert.False(rule.Config.Enabled);
        Assert.Equal(0, rule.Config.DesiredCount);
        Assert.Equal(0, rule.Config.MaxInstances);
        Assert.Equal(0, rule.Config.CooldownSeconds);
        Assert.Equal(0, rule.Config.ScaleDownDelaySeconds);
        var host = storage.Deserialize<HostGrainState>(new BinaryData("""
            {"Name":"drained","IsDraining":true}
            """));
        Assert.Equal(0, host.MaxDockerContainers);
        Assert.Equal(0, host.MaxNativeProcesses);
        Assert.True(host.IsDraining);
    }

    [Fact]
    public void ExplicitLegacyEnabledAndPositiveLimitsArePreserved()
    {
        var options = new OrleansJsonSerializerOptions();
        GrainStorageSerialization.Configure(options);
        var storage = new JsonGrainStorageSerializer(new OrleansJsonSerializer(Options.Create(options)));
        var rule = storage.Deserialize<ProvisioningRuleGrainState>(new BinaryData("""
            {"Config":{"Enabled":true,"DesiredCount":2,"MaxInstances":4,"CooldownSeconds":5,"ScaleDownDelaySeconds":300}}
            """));
        Assert.True(rule.Config.Enabled);
        Assert.Equal(2, rule.Config.DesiredCount);
        Assert.Equal(4, rule.Config.MaxInstances);
        Assert.Equal(5, rule.Config.CooldownSeconds);
        Assert.Equal(300, rule.Config.ScaleDownDelaySeconds);
        var host = storage.Deserialize<HostGrainState>(new BinaryData("""
            {"MaxDockerContainers":2,"MaxNativeProcesses":1,"IsDraining":false}
            """));
        Assert.Equal(2, host.MaxDockerContainers);
        Assert.Equal(1, host.MaxNativeProcesses);
        Assert.False(host.IsDraining);
    }

    [Fact]
    public void DisabledRuleSurvivesWireSerialization()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(ProvisioningRuleGrainState).Assembly));
        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var state = new ProvisioningRuleGrainState
        {
            Config = new ProvisioningRuleConfig { Name = "closed", Enabled = false, DesiredCount = 0 }
        };
        var copy = serializer.Deserialize<ProvisioningRuleGrainState>(serializer.SerializeToArray(state));
        Assert.Equal("closed", copy.Config.Name);
        Assert.False(copy.Config.Enabled);
        Assert.Equal(0, copy.Config.DesiredCount);
    }
}
