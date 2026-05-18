using Orleans;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

public sealed class ProvisioningRuleGrainSyncService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ProvisioningRuleGrainSyncService> _logger;

    public ProvisioningRuleGrainSyncService(
        IGrainFactory grainFactory,
        ILogger<ProvisioningRuleGrainSyncService> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task ConfigureRuleAsync(ProvisioningRule rule)
    {
        var grain = _grainFactory.GetGrain<IProvisioningRuleGrain>(rule.Id, null);
        await grain.SetConfig(ProvisioningRuleGrainConfigMapper.FromRule(rule));

        _logger.LogInformation(
            "Synchronized provisioning rule {RuleId} ({RuleName}) to Orleans grain",
            rule.Id,
            rule.Name);
    }

    public async Task DisableRuleAsync(string ruleId)
    {
        var grain = _grainFactory.GetGrain<IProvisioningRuleGrain>(ruleId, null);
        await grain.Disable();

        _logger.LogInformation("Disabled provisioning rule grain {RuleId}", ruleId);
    }
}

public sealed class ProvisioningRuleGrainStartupSyncService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProvisioningRuleGrainStartupSyncService> _logger;

    public ProvisioningRuleGrainStartupSyncService(
        IServiceProvider serviceProvider,
        ILogger<ProvisioningRuleGrainStartupSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var sync = scope.ServiceProvider.GetRequiredService<ProvisioningRuleGrainSyncService>();
        var rules = (await store.Query<ProvisioningRule>().ToList()).ToList();

        foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sync.ConfigureRuleAsync(rule);
        }

        _logger.LogInformation("Synchronized {Count} provisioning rules to Orleans grains at startup", rules.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class ProvisioningRuleGrainConfigMapper
{
    public static ProvisioningRuleConfig FromRule(ProvisioningRule rule) => new()
    {
        Name = rule.Name,
        Description = rule.Description,
        ProfileId = ResolveProfileId(rule),
        Type = rule.Type,
        Enabled = rule.Enabled,
        DesiredCount = rule.DesiredCount,
        TargetHostId = rule.TargetHostId,
        MinReady = rule.MinReady,
        MaxInstances = rule.MaxInstances,
        ScaleDownDelaySeconds = rule.ScaleDownDelaySeconds,
        WebhookSecret = rule.WebhookSecret,
        AllowedOrgs = [.. rule.AllowedOrgs],
        AllowedRepos = [.. rule.AllowedRepos],
        LabelMappings = [.. rule.LabelMappings.Select(mapping => new LabelMappingConfig
        {
            RequiredLabels = [.. mapping.RequiredLabels],
            ProfileId = mapping.ProfileId,
            Priority = mapping.Priority
        })],
        DefaultProfileId = rule.DefaultProfileId,
        MaxConcurrent = rule.MaxConcurrent,
        CooldownSeconds = rule.CooldownSeconds,
        RequiredHostLabels = new Dictionary<string, string>(rule.RequiredHostLabels),
        TargetGroupId = rule.TargetGroupId,
        CronExpression = rule.CronExpression
    };

    private static string ResolveProfileId(ProvisioningRule rule)
    {
        if (rule.Type != ProvisioningType.Webhook)
            return rule.ProfileId;

        if (!string.IsNullOrWhiteSpace(rule.DefaultProfileId))
            return rule.DefaultProfileId;

        if (!string.IsNullOrWhiteSpace(rule.ProfileId))
            return rule.ProfileId;

        return rule.LabelMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ProfileId))
            .OrderByDescending(mapping => mapping.Priority)
            .Select(mapping => mapping.ProfileId)
            .FirstOrDefault()
            ?? "";
    }
}
