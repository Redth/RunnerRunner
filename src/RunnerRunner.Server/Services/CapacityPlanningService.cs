using RunnerRunner.Core.Models;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

public enum CapacityBlockerKind
{
    None,
    Fifo,
    ProvisioningRule,
    Host,
    Matching,
    Configuration
}

public sealed class CapacityCounter
{
    public CapacityCounter(int used, int limit)
    {
        Used = Math.Max(0, used);
        Limit = Math.Max(0, limit);
    }

    public int Used { get; }
    public int Limit { get; }
    public int Remaining => Math.Max(Limit - Used, 0);
    public bool IsDisabled => Limit <= 0;
    public bool IsSaturated => IsDisabled || Used >= Limit;
    public string Summary => IsDisabled ? "disabled" : $"{Used}/{Limit}";
}

public sealed record ProfileHostUsage(
    string HostId,
    string HostLabel,
    int Used,
    int Limit)
{
    public int Remaining => Math.Max(Limit - Used, 0);
    public bool IsSaturated => Used >= Limit;
    public string Summary => $"{Used}/{Limit}";
}

public sealed record HostProfileUsage(
    string ProfileId,
    string ProfileName,
    int Used,
    int Limit)
{
    public int Remaining => Math.Max(Limit - Used, 0);
    public bool IsSaturated => Used >= Limit;
    public string Summary => $"{Used}/{Limit}";
}

public sealed class HostCapacityView
{
    public required string HostId { get; init; }
    public required string HostLabel { get; init; }
    public int ActiveInstances { get; init; }
    public Dictionary<ExecutionBackend, CapacityCounter> BackendUsage { get; init; } = new();
    public List<HostProfileUsage> ProfileUsage { get; init; } = [];
}

public sealed class RuleRunnerCapacityView
{
    public required string RunnerId { get; init; }
    public required string RunnerName { get; init; }
    public int MatchingHosts { get; init; }
    public int SaturatedHosts { get; init; }
    public int EffectivePoolLimit { get; init; }
    public int AvailableNow { get; init; }
    public int ActiveNow { get; init; }
}

public sealed class RuleCapacityView
{
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public int ConfiguredLimit { get; init; }
    public bool IsUnlimited { get; init; }
    public int ActiveCount { get; init; }
    public int RemainingSlots { get; init; }
    public List<RuleRunnerCapacityView> MappedRunners { get; init; } = [];
}

public sealed class ProfileCapacityView
{
    public required string ProfileId { get; init; }
    public required string ProfileName { get; init; }
    public int PerHostLimit { get; init; }
    public int TotalActive { get; init; }
    public List<ProfileHostUsage> HostUsage { get; init; } = [];
    public int SaturatedHostCount => HostUsage.Count(h => h.IsSaturated);
}

public sealed class HostCandidateView
{
    public required string HostId { get; init; }
    public required string HostLabel { get; init; }
    public required CapacityCounter BackendCapacity { get; init; }
    public int TotalHostLoad { get; init; }
    public bool CanRunNow { get; init; }
    public CapacityBlockerKind BlockedBy { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class HostSelectionAnalysis
{
    public Host? SelectedHost { get; init; }
    public bool CapacityBlocked { get; init; }
    public CapacityBlockerKind BlockedBy { get; init; }
    public string Reason { get; init; } = "";
    public List<HostCandidateView> Candidates { get; init; } = [];
}

public sealed class EventCapacityView
{
    public CapacityBlockerKind BlockedBy { get; init; }
    public string Summary { get; init; } = "";
    public List<string> Details { get; init; } = [];
}

public sealed class CapacitySnapshot
{
    public static CapacitySnapshot Empty { get; } = new();

    public Dictionary<string, HostCapacityView> Hosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RuleCapacityView> Rules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProfileCapacityView> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EventCapacityView> Events { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CapacityPlanningService
{
    private sealed record QueuedWorkBlocker(WebhookEvent Event, RunnerProfile Profile);

    private readonly IDocumentStore _store;

    public CapacityPlanningService(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<CapacitySnapshot> GetSnapshotAsync()
    {
        var hosts = (await _store.Query<Host>().ToList()).ToList();
        var profiles = (await _store.Query<RunnerProfile>().ToList()).ToList();
        var rules = (await _store.Query<ProvisioningRule>().ToList()).ToList();
        var instances = (await _store.Query<RunnerInstance>().ToList()).ToList();
        var events = (await _store.Query<WebhookEvent>().ToList()).ToList();

        return BuildSnapshot(hosts, profiles, rules, instances, events);
    }

    public static CapacitySnapshot BuildSnapshot(
        IReadOnlyCollection<Host> hosts,
        IReadOnlyCollection<RunnerProfile> profiles,
        IReadOnlyCollection<ProvisioningRule> rules,
        IReadOnlyCollection<RunnerInstance> instances,
        IReadOnlyCollection<WebhookEvent> events)
    {
        var rulesById = rules.ToDictionary(r => r.Id, r => r, StringComparer.OrdinalIgnoreCase);
        var profilesById = profiles.ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        ProvisioningRuleRunnerResolver.AddMaterializedRunnerProfiles(profilesById, rules);
        var activeInstances = instances.Where(IsCapacityConsuming).ToList();

        var hostViews = hosts.ToDictionary(
            host => host.Id,
            host => BuildHostView(host, activeInstances, profilesById),
            StringComparer.OrdinalIgnoreCase);

        var profileViews = profilesById.Values.ToDictionary(
            profile => profile.Id,
            profile => BuildProfileView(profile, hosts, activeInstances),
            StringComparer.OrdinalIgnoreCase);

        var ruleViews = rules.ToDictionary(
            rule => rule.Id,
            rule => EvaluateRuleCapacity(rule, hosts, profilesById, activeInstances, events),
            StringComparer.OrdinalIgnoreCase);

        var eventViews = events.ToDictionary(
            evt => evt.Id,
            evt => ExplainEvent(evt, hosts, profilesById, rulesById, activeInstances, events),
            StringComparer.OrdinalIgnoreCase);

        return new CapacitySnapshot
        {
            Hosts = hostViews,
            Profiles = profileViews,
            Rules = ruleViews,
            Events = eventViews
        };
    }

    public static bool IsRunnerRunnerManaged(RunnerInstance instance)
    {
        if (instance.ManagedByRunnerRunner.HasValue)
            return instance.ManagedByRunnerRunner.Value;

        if (string.IsNullOrWhiteSpace(instance.RunnerName) || string.IsNullOrWhiteSpace(instance.HostId))
            return false;

        if (!string.IsNullOrWhiteSpace(instance.ContainerId)
            || !string.IsNullOrWhiteSpace(instance.VmName))
            return true;

        if (!string.IsNullOrWhiteSpace(instance.WebhookEventId)
            || !string.IsNullOrWhiteSpace(instance.JitConfig)
            || string.Equals(instance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.ProvisioningMode, "webhook", StringComparison.OrdinalIgnoreCase)
            || instance.RunnerName.Contains("-jit-", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool IsCapacityConsuming(RunnerInstance instance) =>
        IsRunnerRunnerManaged(instance)
        && (instance.Status is RunnerInstanceStatus.Pending
            or RunnerInstanceStatus.Starting
            or RunnerInstanceStatus.Running
            or RunnerInstanceStatus.Stopping);

    public static RuleCapacityView EvaluateRuleCapacity(
        ProvisioningRule rule,
        IReadOnlyCollection<Host> hosts,
        IReadOnlyDictionary<string, RunnerProfile> profilesById,
        IReadOnlyCollection<RunnerInstance> instances,
        IReadOnlyCollection<WebhookEvent> events)
    {
        var runnerIds = rule.GetRunnerProfileIds()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var activeInstances = instances.Where(IsCapacityConsuming).ToList();
        var relatedEventIds = events
            .Where(e => string.Equals(e.BindingId, rule.Id, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int activeCount;
        if (rule.Type == ProvisioningType.Webhook)
        {
            activeCount = activeInstances.Count(i =>
                string.Equals(i.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
                && ((!string.IsNullOrWhiteSpace(i.WebhookEventId) && relatedEventIds.Contains(i.WebhookEventId))
                    || (string.IsNullOrWhiteSpace(i.WebhookEventId) && runnerIds.Any(id => InstanceMatchesRunner(i, id)))));
        }
        else
        {
            activeCount = activeInstances.Count(i =>
                runnerIds.Any(id => InstanceMatchesRunner(i, id))
                && hosts.Any(h =>
                    string.Equals(h.Id, i.HostId, StringComparison.OrdinalIgnoreCase)
                    && MatchesRuleHostRequirements(h, rule)
                    && (!profilesById.TryGetValue(GetInstanceRunnerId(i), out var instanceProfile)
                        || MatchesProfileHostRequirements(h, instanceProfile))));
        }

        var configuredLimit = rule.Type switch
        {
            ProvisioningType.Static => Math.Max(0, rule.DesiredCount),
            ProvisioningType.ScaleSet => Math.Max(0, rule.MaxInstances),
            ProvisioningType.Webhook => Math.Max(0, rule.MaxConcurrent),
            _ => Math.Max(0, rule.DesiredCount)
        };
        var isUnlimited = rule.Type == ProvisioningType.Webhook && configuredLimit == 0;

        var mappedRunners = runnerIds
            .Select(id => profilesById.TryGetValue(id, out var profile) ? profile : null)
            .Where(profile => profile != null)
            .Select(profile => BuildRuleRunnerView(profile!, rule, hosts, activeInstances, profilesById))
            .OrderBy(view => view.RunnerName)
            .ToList();

        return new RuleCapacityView
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            ConfiguredLimit = configuredLimit,
            IsUnlimited = isUnlimited,
            ActiveCount = activeCount,
            RemainingSlots = isUnlimited ? int.MaxValue : Math.Max(configuredLimit - activeCount, 0),
            MappedRunners = mappedRunners
        };
    }

    public static HostSelectionAnalysis AnalyzeHostSelection(
        RunnerProfile profile,
        ProvisioningRule? rule,
        IReadOnlyCollection<Host> hosts,
        IReadOnlyDictionary<string, RunnerProfile> profilesById,
        IReadOnlyCollection<RunnerInstance> instances,
        bool requireDispatchReadiness = false)
    {
        var backendName = profile.ExecutionBackend.ToString().ToLowerInvariant();
        var matchingHosts = hosts
            .Where(host =>
                HostCanProvideRunnerPlatform(host, profile)
                && MatchesRuleHostRequirements(host, rule)
                && MatchesProfileHostRequirements(host, profile)
                && GetBackendLimit(host, profile.ExecutionBackend) > 0)
            .ToList();

        if (matchingHosts.Count == 0)
        {
            return new HostSelectionAnalysis
            {
                CapacityBlocked = false,
                BlockedBy = CapacityBlockerKind.Matching,
                Reason = $"No host matches target platform '{profile.RequiredHostPlatform}', backend '{backendName}' capacity, and the rule target filters"
            };
        }

        var candidates = matchingHosts
            .Select(host =>
            {
                var hostInstances = instances
                    .Where(i => string.Equals(i.HostId, host.Id, StringComparison.OrdinalIgnoreCase) && IsCapacityConsuming(i))
                    .ToList();

                var backendUsage = GetBackendUsage(host, profile.ExecutionBackend, hostInstances, profilesById);
                var canRunNow = true;
                var blockedBy = CapacityBlockerKind.None;
                var detail = $"Ready now: {backendName} {backendUsage.Summary}";

                if (requireDispatchReadiness && host.AgentStatus != AgentStatus.Online)
                {
                    canRunNow = false;
                    blockedBy = CapacityBlockerKind.Matching;
                    detail = $"HostWorker is {host.AgentStatus.ToString().ToLowerInvariant()}";
                }
                else if (backendUsage.IsSaturated)
                {
                    canRunNow = false;
                    blockedBy = CapacityBlockerKind.Host;
                    detail = $"Host {backendName} slots full: {backendUsage.Summary}";
                }

                return new HostCandidateView
                {
                    HostId = host.Id,
                    HostLabel = host.Label,
                    BackendCapacity = backendUsage,
                    TotalHostLoad = hostInstances.Count,
                    CanRunNow = canRunNow,
                    BlockedBy = blockedBy,
                    Detail = detail
                };
            })
            .OrderBy(candidate => candidate.CanRunNow ? 0 : 1)
            .ThenBy(candidate => candidate.TotalHostLoad)
            .ThenBy(candidate => candidate.HostLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectedCandidate = candidates.FirstOrDefault(c => c.CanRunNow);
        if (selectedCandidate != null)
        {
            return new HostSelectionAnalysis
            {
                SelectedHost = matchingHosts.First(host => string.Equals(host.Id, selectedCandidate.HostId, StringComparison.OrdinalIgnoreCase)),
                CapacityBlocked = false,
                BlockedBy = CapacityBlockerKind.None,
                Reason = $"Selected host '{selectedCandidate.HostLabel}'",
                Candidates = candidates
            };
        }

        return new HostSelectionAnalysis
        {
            CapacityBlocked = candidates.All(candidate => candidate.BlockedBy == CapacityBlockerKind.Host),
            BlockedBy = candidates.All(candidate => candidate.BlockedBy == CapacityBlockerKind.Host)
                ? CapacityBlockerKind.Host
                : CapacityBlockerKind.Matching,
            Reason = BuildHostSelectionFailureReason(backendName, candidates),
            Candidates = candidates
        };
    }

    public static bool HasEarlierQueuedWorkAhead(
        WebhookEvent currentEvent,
        ProvisioningRule? currentRule,
        RunnerProfile currentProfile,
        IReadOnlyCollection<WebhookEvent> allEvents,
        IReadOnlyDictionary<string, ProvisioningRule> rulesById,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
        => GetEarlierQueuedWorkAhead(
            currentEvent,
            currentRule,
            currentProfile,
            allEvents,
            rulesById,
            profilesById).Count > 0;

    private static List<string> DescribeEarlierQueuedWorkAhead(
        WebhookEvent currentEvent,
        ProvisioningRule? currentRule,
        RunnerProfile currentProfile,
        IReadOnlyCollection<WebhookEvent> allEvents,
        IReadOnlyDictionary<string, ProvisioningRule> rulesById,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
    {
        var blockers = GetEarlierQueuedWorkAhead(
            currentEvent,
            currentRule,
            currentProfile,
            allEvents,
            rulesById,
            profilesById);

        var details = blockers
            .Take(5)
            .Select(FormatQueuedWorkBlocker)
            .ToList();

        if (blockers.Count > details.Count)
            details.Add($"...and {blockers.Count - details.Count} more older queued job(s)");

        return details;
    }

    private static List<QueuedWorkBlocker> GetEarlierQueuedWorkAhead(
        WebhookEvent currentEvent,
        ProvisioningRule? currentRule,
        RunnerProfile currentProfile,
        IReadOnlyCollection<WebhookEvent> allEvents,
        IReadOnlyDictionary<string, ProvisioningRule> rulesById,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
    {
        var blockers = new List<QueuedWorkBlocker>();
        var queuedEvents = allEvents
            .Where(e =>
                e.Action == "queued"
                && e.Id != currentEvent.Id
                && e.JobId != currentEvent.JobId
                && !e.IsTerminal
                && e.Status is not "provisioned")
            .OrderBy(e => e.ReceivedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var earlierEvent in queuedEvents)
        {
            var isEarlier =
                earlierEvent.ReceivedAt < currentEvent.ReceivedAt
                || (earlierEvent.ReceivedAt == currentEvent.ReceivedAt
                    && string.CompareOrdinal(earlierEvent.Id, currentEvent.Id) < 0);

            if (!isEarlier || earlierEvent.Status is "no_match" or "pending_config")
                continue;

            var (earlierRule, earlierProfile, _) = ResolveProvisioningMatch(
                earlierEvent,
                earlierEvent.MatchedProfileId,
                rulesById.Values,
                profilesById);

            if (earlierProfile == null)
                continue;

            if (SharesCapacityLane(currentRule, currentProfile, earlierRule, earlierProfile))
                blockers.Add(new QueuedWorkBlocker(earlierEvent, earlierProfile));
        }

        return blockers;
    }

    private static bool SharesCapacityLane(
        ProvisioningRule? currentRule,
        RunnerProfile currentProfile,
        ProvisioningRule? earlierRule,
        RunnerProfile earlierProfile)
    {
        if (earlierProfile.RequiredHostPlatform != currentProfile.RequiredHostPlatform
            || earlierProfile.ExecutionBackend != currentProfile.ExecutionBackend)
        {
            return false;
        }

        if (!CanHostRoutingOverlap(currentRule, currentProfile, earlierRule, earlierProfile))
            return false;

        return true;
    }

    private static bool CanHostRoutingOverlap(
        ProvisioningRule? currentRule,
        RunnerProfile currentProfile,
        ProvisioningRule? earlierRule,
        RunnerProfile earlierProfile)
    {
        if (!string.IsNullOrWhiteSpace(currentRule?.TargetHostId)
            && !string.IsNullOrWhiteSpace(earlierRule?.TargetHostId)
            && !string.Equals(currentRule.TargetHostId, earlierRule.TargetHostId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentGroup = GetRequiredGroupId(currentRule, currentProfile, out var currentGroupConflict);
        var earlierGroup = GetRequiredGroupId(earlierRule, earlierProfile, out var earlierGroupConflict);
        if (currentGroupConflict || earlierGroupConflict)
            return false;

        if (!string.IsNullOrWhiteSpace(currentGroup)
            && !string.IsNullOrWhiteSpace(earlierGroup)
            && !string.Equals(currentGroup, earlierGroup, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !RequiredLabelsConflict(currentRule?.RequiredHostLabels, earlierRule?.RequiredHostLabels);
    }

    private static string? GetRequiredGroupId(ProvisioningRule? rule, RunnerProfile profile, out bool hasConflict)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(rule?.TargetGroupId))
            groups.Add(rule.TargetGroupId.Trim());
        if (!string.IsNullOrWhiteSpace(profile.TargetGroupId))
            groups.Add(profile.TargetGroupId.Trim());

        hasConflict = groups.Count > 1;
        return groups.Count == 1 ? groups.Single() : null;
    }

    private static bool RequiredLabelsConflict(
        IReadOnlyDictionary<string, string>? currentLabels,
        IReadOnlyDictionary<string, string>? earlierLabels)
    {
        if (currentLabels == null || earlierLabels == null)
            return false;

        foreach (var current in currentLabels)
        {
            var earlier = earlierLabels.FirstOrDefault(label => string.Equals(label.Key, current.Key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(earlier.Key)
                && !string.Equals(earlier.Value, current.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildFifoSummary(int blockerCount)
        => blockerCount switch
        {
            0 => "Waiting for older queued work in the same capacity lane",
            1 => "Waiting for 1 older queued job in the same capacity lane",
            _ => $"Waiting for {blockerCount} older queued jobs in the same capacity lane"
        };

    private static string FormatQueuedWorkBlocker(QueuedWorkBlocker blocker)
    {
        var evt = blocker.Event;
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(evt.JobId))
            parts.Add($"Job {evt.JobId}");
        if (!string.IsNullOrWhiteSpace(evt.Repository))
            parts.Add(evt.Repository);
        if (!string.IsNullOrWhiteSpace(evt.WorkflowName))
            parts.Add(evt.WorkflowName);
        parts.Add($"target {blocker.Profile.Name}");
        if (!string.IsNullOrWhiteSpace(evt.Status))
            parts.Add($"status {evt.Status}");
        if (!string.IsNullOrWhiteSpace(evt.Error))
            parts.Add(evt.Error);

        return string.Join(" · ", parts);
    }

    private static List<string> BuildRuleCapacityDetails(RuleCapacityView ruleView, RunnerProfile currentProfile)
    {
        var details = new List<string>
        {
            $"Max concurrent: {ruleView.ActiveCount}/{ruleView.ConfiguredLimit} active for rule '{ruleView.RuleName}' ({ruleView.RemainingSlots} remaining)"
        };

        var mappedRunners = ruleView.MappedRunners
            .OrderBy(view => string.Equals(view.RunnerId, currentProfile.Id, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(view => view.RunnerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var runner in mappedRunners.Take(5))
        {
            details.Add(
                $"Target '{runner.RunnerName}': {runner.ActiveNow} active, {runner.AvailableNow} host slot(s) available across {runner.MatchingHosts} matching host(s)");
        }

        if (mappedRunners.Count > 5)
            details.Add($"...and {mappedRunners.Count - 5} more target(s)");

        return details;
    }

    public static EventCapacityView ExplainEvent(
        WebhookEvent evt,
        IReadOnlyCollection<Host> hosts,
        IReadOnlyDictionary<string, RunnerProfile> profilesById,
        IReadOnlyDictionary<string, ProvisioningRule> rulesById,
        IReadOnlyCollection<RunnerInstance> instances,
        IReadOnlyCollection<WebhookEvent> allEvents)
    {
        if (evt.Action != "queued")
            return new EventCapacityView();

        if (evt.Status == "pending_fifo")
        {
            var (fifoRule, fifoProfile, _) = ResolveProvisioningMatch(evt, evt.MatchedProfileId, rulesById.Values, profilesById);
            var details = fifoProfile == null
                ? []
                : DescribeEarlierQueuedWorkAhead(evt, fifoRule, fifoProfile, allEvents, rulesById, profilesById);

            return new EventCapacityView
            {
                BlockedBy = CapacityBlockerKind.Fifo,
                Summary = BuildFifoSummary(details.Count),
                Details = details
            };
        }

        if (evt.Status == "pending_config")
        {
            return new EventCapacityView
            {
                BlockedBy = CapacityBlockerKind.Configuration,
                Summary = evt.Error ?? "Provisioning is waiting on a missing or invalid configuration dependency"
            };
        }

        var (rule, profile, reason) = ResolveProvisioningMatch(evt, evt.MatchedProfileId, rulesById.Values, profilesById);
        if (profile == null)
        {
            return new EventCapacityView
            {
                BlockedBy = CapacityBlockerKind.Matching,
                Summary = reason
            };
        }

        var fifoDetails = DescribeEarlierQueuedWorkAhead(evt, rule, profile, allEvents, rulesById, profilesById);
        if (fifoDetails.Count > 0)
        {
            return new EventCapacityView
            {
                BlockedBy = CapacityBlockerKind.Fifo,
                Summary = BuildFifoSummary(fifoDetails.Count),
                Details = fifoDetails
            };
        }

        if (rule != null)
        {
            var ruleView = EvaluateRuleCapacity(rule, hosts, profilesById, instances, allEvents);
            if (!ruleView.IsUnlimited && ruleView.RemainingSlots <= 0)
            {
                return new EventCapacityView
                {
                    BlockedBy = CapacityBlockerKind.ProvisioningRule,
                    Summary = $"Rule '{rule.Name}' is using {ruleView.ActiveCount}/{ruleView.ConfiguredLimit} concurrent slots",
                    Details = BuildRuleCapacityDetails(ruleView, profile)
                };
            }
        }

        var hostAnalysis = AnalyzeHostSelection(profile, rule, hosts, profilesById, instances);
        var detail = hostAnalysis.Candidates
            .Take(3)
            .Select(candidate =>
                $"{candidate.HostLabel}: {profile.ExecutionBackend.ToString().ToLowerInvariant()} {candidate.BackendCapacity.Summary}")
            .ToList();

        if (hostAnalysis.SelectedHost != null)
        {
            return new EventCapacityView
            {
                BlockedBy = CapacityBlockerKind.None,
                Summary = $"Capacity is available on '{hostAnalysis.SelectedHost.Label}'"
            };
        }

        return new EventCapacityView
        {
            BlockedBy = hostAnalysis.BlockedBy,
            Summary = hostAnalysis.Reason,
            Details = detail
        };
    }

    public static bool MatchesRuleHostRequirements(Host host, ProvisioningRule? rule)
    {
        if (rule == null)
            return true;

        return MatchesHostRouting(
            host,
            rule.TargetHostId,
            rule.TargetGroupId,
            rule.RequiredHostLabels,
            Array.Empty<string>());
    }

    private static HostCapacityView BuildHostView(
        Host host,
        IReadOnlyCollection<RunnerInstance> instances,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
    {
        var hostInstances = instances
            .Where(i => string.Equals(i.HostId, host.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var profileUsage = hostInstances
            .Where(i => profilesById.TryGetValue(i.ProfileId, out _))
            .GroupBy(i => i.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var profile = profilesById[group.Key];
                return new HostProfileUsage(profile.Id, profile.Name, group.Count(), GetBackendLimit(host, profile.ExecutionBackend));
            })
            .OrderByDescending(view => view.Used)
            .ToList();

        return new HostCapacityView
        {
            HostId = host.Id,
            HostLabel = host.Label,
            ActiveInstances = hostInstances.Count,
            BackendUsage = new Dictionary<ExecutionBackend, CapacityCounter>
            {
                [ExecutionBackend.Docker] = GetBackendUsage(host, ExecutionBackend.Docker, hostInstances, profilesById),
                [ExecutionBackend.Tart] = GetBackendUsage(host, ExecutionBackend.Tart, hostInstances, profilesById),
                [ExecutionBackend.Native] = GetBackendUsage(host, ExecutionBackend.Native, hostInstances, profilesById)
            },
            ProfileUsage = profileUsage
        };
    }

    private static ProfileCapacityView BuildProfileView(
        RunnerProfile profile,
        IReadOnlyCollection<Host> hosts,
        IReadOnlyCollection<RunnerInstance> instances)
    {
        var usages = instances
            .Where(i => string.Equals(i.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.HostId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var host = hosts.FirstOrDefault(h => string.Equals(h.Id, group.Key, StringComparison.OrdinalIgnoreCase));
                var hostLabel = host?.Label ?? group.Key;
                var limit = host == null ? 0 : GetBackendLimit(host, profile.ExecutionBackend);
                return new ProfileHostUsage(group.Key, hostLabel, group.Count(), limit);
            })
            .OrderByDescending(view => view.Used)
            .ThenBy(view => view.HostLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileCapacityView
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            PerHostLimit = 0,
            TotalActive = usages.Sum(view => view.Used),
            HostUsage = usages
        };
    }

    private static RuleRunnerCapacityView BuildRuleRunnerView(
        RunnerProfile profile,
        ProvisioningRule rule,
        IReadOnlyCollection<Host> hosts,
        IReadOnlyCollection<RunnerInstance> instances,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
    {
        var matchingHosts = hosts
            .Where(host =>
                HostCanProvideRunnerPlatform(host, profile)
                && MatchesRuleHostRequirements(host, rule)
                && MatchesProfileHostRequirements(host, profile)
                && GetBackendLimit(host, profile.ExecutionBackend) > 0)
            .ToList();

        var matchingHostUsages = matchingHosts
            .Select(host =>
            {
                var hostInstances = instances
                    .Where(i => string.Equals(i.HostId, host.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var backendUsage = GetBackendUsage(host, profile.ExecutionBackend, hostInstances, profilesById);
                var activeRunnerInstances = hostInstances.Count(i => InstanceMatchesRunner(i, profile.Id));
                return new { backendUsage, activeRunnerInstances };
            })
            .ToList();

        var effectivePoolLimit = matchingHostUsages.Sum(x => x.backendUsage.Limit);
        var availableNow = matchingHostUsages.Sum(x => x.backendUsage.Remaining);
        var activeNow = matchingHostUsages.Sum(x => x.activeRunnerInstances);

        return new RuleRunnerCapacityView
        {
            RunnerId = profile.Id,
            RunnerName = profile.Name,
            MatchingHosts = matchingHosts.Count,
            SaturatedHosts = matchingHostUsages.Count(x => x.backendUsage.IsSaturated),
            EffectivePoolLimit = effectivePoolLimit,
            AvailableNow = availableNow,
            ActiveNow = activeNow
        };
    }

    private static CapacityCounter GetBackendUsage(
        Host host,
        ExecutionBackend backend,
        IReadOnlyCollection<RunnerInstance> hostInstances,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
    {
        var used = hostInstances.Count(i =>
            profilesById.TryGetValue(GetInstanceRunnerId(i), out var instanceProfile)
            && instanceProfile.ExecutionBackend == backend);

        if (backend == ExecutionBackend.Tart && host.ObservedRunningTartVMs is int observedRunningTartVMs)
            used = Math.Max(used, observedRunningTartVMs);

        var limit = GetBackendLimit(host, backend);

        return new CapacityCounter(used, limit);
    }

    public static int GetBackendLimit(Host host, ExecutionBackend backend) =>
        backend switch
        {
            ExecutionBackend.Docker => host.MaxDockerContainers,
            ExecutionBackend.Tart => host.MaxTartVMs,
            _ => host.MaxNativeProcesses
        };

    private static bool HostCanProvideRunnerPlatform(Host host, RunnerProfile profile)
    {
        if (profile.ExecutionBackend != ExecutionBackend.Docker)
            return host.Platform == profile.RequiredHostPlatform;

        var expectedDockerOs = GetExpectedDockerOs(profile.RequiredHostPlatform);
        if (expectedDockerOs == null)
            return host.Platform == profile.RequiredHostPlatform;

        if (TryGetHostLabelValue(host, "docker_os", out var dockerOs) && !string.IsNullOrWhiteSpace(dockerOs))
            return string.Equals(dockerOs, expectedDockerOs, StringComparison.OrdinalIgnoreCase);

        if (expectedDockerOs == "linux")
            return host.Platform is HostPlatform.Linux or HostPlatform.MacOS;

        return host.Platform == profile.RequiredHostPlatform;
    }

    private static string BuildHostSelectionFailureReason(string backendName, IReadOnlyCollection<HostCandidateView> candidates)
    {
        var details = candidates
            .Take(3)
            .Select(candidate => $"{candidate.HostLabel}: {candidate.Detail}")
            .ToList();

        if (candidates.All(candidate => candidate.BlockedBy == CapacityBlockerKind.Host))
            return details.Count == 0
                ? $"All matching hosts are currently out of {backendName} capacity"
                : $"All matching hosts are currently out of {backendName} capacity ({string.Join(" · ", details)})";

        return details.Count == 0
            ? $"No matching host is ready for backend '{backendName}'"
            : $"No matching host is ready for backend '{backendName}' ({string.Join(" · ", details)})";
    }

    private static bool InstanceMatchesRunner(RunnerInstance instance, string runnerId) =>
        string.Equals(instance.RunnerDefinitionId, runnerId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(instance.ProfileId, runnerId, StringComparison.OrdinalIgnoreCase);

    private static string GetInstanceRunnerId(RunnerInstance instance) =>
        !string.IsNullOrWhiteSpace(instance.RunnerDefinitionId)
            ? instance.RunnerDefinitionId
            : instance.ProfileId;

    private static bool MatchesProfileHostRequirements(Host host, RunnerProfile profile)
    {
        if (!MatchesHostRouting(host, null, profile.TargetGroupId, null, profile.RequiredHostCapabilities))
            return false;

        if (profile.ExecutionBackend != ExecutionBackend.Docker)
            return true;

        if (!TryGetHostLabelValue(host, "docker_os", out var dockerOs) || string.IsNullOrWhiteSpace(dockerOs))
            return true;

        var expectedDockerOs = GetExpectedDockerOs(profile.RequiredHostPlatform);

        return expectedDockerOs == null
            || string.Equals(dockerOs, expectedDockerOs, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetExpectedDockerOs(HostPlatform platform) =>
        platform switch
        {
            HostPlatform.Windows => "windows",
            HostPlatform.Linux => "linux",
            _ => null
        };

    private static bool MatchesHostRouting(
        Host host,
        string? targetHostId,
        string? targetGroupId,
        IReadOnlyDictionary<string, string>? requiredHostLabels,
        IReadOnlyCollection<string> requiredHostCapabilities)
    {
        if (!string.IsNullOrWhiteSpace(targetHostId)
            && !string.Equals(host.Id, targetHostId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetGroupId)
            && !string.Equals(host.GroupId, targetGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requiredHostLabels != null)
        {
            foreach (var required in requiredHostLabels)
            {
                if (!TryGetHostLabelValue(host, required.Key, out var actualValue)
                    || !string.Equals(actualValue, required.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        if (requiredHostCapabilities.Count == 0)
            return true;

        var hostCapabilities = GetEffectiveHostCapabilities(host);
        return requiredHostCapabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .All(hostCapabilities.Contains);
    }

    private static bool TryGetHostLabelValue(Host host, string key, out string value)
    {
        if (host.Labels.TryGetValue(key, out value!))
            return true;

        var match = host.Labels.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
        value = match.Value;
        return !string.IsNullOrEmpty(match.Key);
    }

    private static HashSet<string> GetEffectiveHostCapabilities(Host host)
    {
        var capabilities = host.Capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        capabilities.Add("native");
        capabilities.Add(host.Platform.ToString().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(host.Architecture))
            capabilities.Add(host.Architecture.Trim().ToLowerInvariant());

        return capabilities;
    }

    private static (ProvisioningRule? Rule, RunnerProfile? Profile, string Reason) ResolveProvisioningMatch(
        WebhookEvent evt,
        string? requestedProfileId,
        IEnumerable<ProvisioningRule> rules,
        IReadOnlyDictionary<string, RunnerProfile> profilesById)
    {
        if (!Enum.TryParse<RunnerProvider>(evt.Provider, true, out var provider))
            return (null, null, $"Unsupported provider '{evt.Provider}'");

        var repo = evt.Repository;
        var org = repo.Contains('/') ? repo.Split('/')[0] : "";

        var candidateRules = rules
            .Where(r => r.Type == ProvisioningType.Webhook && r.Provider == provider && r.Enabled)
            .Where(r =>
                r.AllowedRepos.Any(x => x.Equals(repo, StringComparison.OrdinalIgnoreCase))
                || r.AllowedOrgs.Any(x => x.Equals(org, StringComparison.OrdinalIgnoreCase))
                || (r.AllowedRepos.Count == 0 && r.AllowedOrgs.Count == 0))
            .OrderByDescending(r => string.Equals(r.Id, evt.BindingId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidateRules.Count == 0)
            return (null, null, $"No provisioning rule currently matches repository '{repo}'");

        foreach (var rule in candidateRules)
        {
            if (rule.RunnerDefinitions.Count > 0)
            {
                var runnerDefinition = rule.ResolveWebhookRunnerDefinition(evt.Labels, requestedProfileId);
                if (runnerDefinition != null)
                    return (rule, runnerDefinition.ToProfile(rule), "");

                continue;
            }

            var profileId = rule.ResolveWebhookProfileId(evt.Labels, requestedProfileId);
            if (string.IsNullOrWhiteSpace(profileId))
                continue;

            if (profilesById.TryGetValue(profileId, out var profile))
                return (rule, profile, "");
        }

        return (candidateRules[0], null, $"No current label mapping matches labels [{string.Join(", ", evt.Labels)}]");
    }
}
