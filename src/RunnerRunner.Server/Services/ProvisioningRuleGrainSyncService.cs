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
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProvisioningRuleGrainStartupSyncService> _logger;
    private readonly TimeSpan _retryDelay;
    private CancellationTokenSource? _stoppingCts;
    private Task? _syncTask;

    public ProvisioningRuleGrainStartupSyncService(
        IServiceProvider serviceProvider,
        ILogger<ProvisioningRuleGrainStartupSyncService> logger)
        : this(serviceProvider, logger, DefaultRetryDelay)
    {
    }

    public ProvisioningRuleGrainStartupSyncService(
        IServiceProvider serviceProvider,
        ILogger<ProvisioningRuleGrainStartupSyncService> logger,
        TimeSpan retryDelay)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _retryDelay = retryDelay;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _syncTask = RunStartupSyncUntilSuccessfulAsync(_stoppingCts.Token);
        return Task.CompletedTask;
    }

    public async Task<int> SynchronizeOnceAsync(CancellationToken cancellationToken)
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

        return rules.Count;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts == null || _syncTask == null)
            return;

        await _stoppingCts.CancelAsync();

        try
        {
            await _syncTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stoppingCts.IsCancellationRequested)
        {
        }
        finally
        {
            _stoppingCts.Dispose();
            _stoppingCts = null;
            _syncTask = null;
        }
    }

    private async Task RunStartupSyncUntilSuccessfulAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                var ruleCount = await SynchronizeOnceAsync(cancellationToken);
                _logger.LogInformation(
                    "Synchronized {Count} provisioning rules to Orleans grains at startup on attempt {Attempt}",
                    ruleCount,
                    attempt);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provisioning rule grain startup sync attempt {Attempt} failed; retrying in {RetryDelay}",
                    attempt,
                    _retryDelay);
            }

            try
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
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
        Provider = rule.Provider,
        ProviderCredentialId = rule.ProviderCredentialId,
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
        CronExpression = rule.CronExpression,
        RunnerDefinitions = [.. rule.RunnerDefinitions.Select(CloneRunnerDefinition)]
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

    private static RunnerDefinition CloneRunnerDefinition(RunnerDefinition runner) => new()
    {
        Id = runner.Id,
        Name = runner.Name,
        TargetKey = runner.TargetKey,
        Description = runner.Description,
        Enabled = runner.Enabled,
        RequiredHostPlatform = runner.RequiredHostPlatform,
        ExecutionBackend = runner.ExecutionBackend,
        RunnerAgentVersion = runner.RunnerAgentVersion,
        EnvironmentVariableSetIds = [.. runner.EnvironmentVariableSetIds],
        EnvironmentOverrides = new Dictionary<string, string>(runner.EnvironmentOverrides),
        EnvironmentOverrideSecretKeys = [.. runner.EnvironmentOverrideSecretKeys],
        DockerConfig = runner.DockerConfig,
        TartConfig = runner.TartConfig,
        Labels = [.. runner.Labels],
        TargetGroupId = runner.TargetGroupId,
        RequiredHostCapabilities = [.. runner.RequiredHostCapabilities],
        RunnerGroup = runner.RunnerGroup,
        Ephemeral = runner.Ephemeral,
        ProviderConfig = new Dictionary<string, string>(runner.ProviderConfig),
        EmitMetadataLabels = runner.EmitMetadataLabels,
        EmitJobStartedBanner = runner.EmitJobStartedBanner,
        AllowWebhookImageTagOverride = runner.AllowWebhookImageTagOverride,
        Matchers = [.. runner.Matchers.Select(m => new RunnerLabelMatcher
        {
            Id = m.Id,
            RequiredLabels = [.. m.RequiredLabels],
            Priority = m.Priority,
            Enabled = m.Enabled
        })],
        InitStepRefs = [.. runner.InitStepRefs.Select(r => new RunnerInitStepRef
        {
            InitStepId = r.InitStepId,
            Order = r.Order,
            EnabledOverride = r.EnabledOverride,
            TimeoutSecondsOverride = r.TimeoutSecondsOverride,
            EnvironmentOverrides = new Dictionary<string, string>(r.EnvironmentOverrides),
            EnvironmentOverrideSecretKeys = [.. r.EnvironmentOverrideSecretKeys]
        })],
        InlineInitSteps = [.. runner.InlineInitSteps.Select(RunnerDefinition.CloneInitStep)],
        CreatedAt = runner.CreatedAt,
        UpdatedAt = runner.UpdatedAt
    };
}
