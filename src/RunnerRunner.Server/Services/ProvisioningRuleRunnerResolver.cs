using RunnerRunner.Core.Models;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

public static class ProvisioningRuleRunnerResolver
{
    public static void AddMaterializedRunnerProfiles(
        IDictionary<string, RunnerProfile> profilesById,
        IEnumerable<ProvisioningRule> rules)
    {
        foreach (var profile in MaterializeRuleRunnerProfiles(rules))
            profilesById[profile.Id] = profile;
    }

    public static IEnumerable<RunnerProfile> MaterializeRuleRunnerProfiles(IEnumerable<ProvisioningRule> rules)
    {
        foreach (var rule in rules)
        {
            foreach (var runner in rule.RunnerDefinitions.Where(r => r.Enabled))
                yield return runner.ToProfile(rule);
        }
    }

    public static async Task<(RunnerDefinition? RunnerDefinition, RunnerProfile? Profile)> ResolveProfileAsync(
        IDocumentStore store,
        ProvisioningRule rule,
        IEnumerable<string>? labels = null,
        string? preferredRunnerOrProfileId = null)
    {
        if (rule.RunnerDefinitions.Count > 0)
        {
            var runnerDefinition = rule.ResolveRunnerDefinition(labels, preferredRunnerOrProfileId);
            if (runnerDefinition == null)
                return (null, null);

            var initSteps = await MaterializeInitStepsAsync(store, runnerDefinition);
            return (runnerDefinition, runnerDefinition.ToProfile(rule, initSteps));
        }

        var profileId = rule.Type == ProvisioningType.Webhook
            ? rule.ResolveWebhookProfileId(labels ?? [], preferredRunnerOrProfileId)
            : rule.ProfileId;

        if (string.IsNullOrWhiteSpace(profileId))
            return (null, null);

        var profile = await store.Get<RunnerProfile>(profileId);
        return (null, profile);
    }

    public static async Task<List<RunnerInitStep>> MaterializeInitStepsAsync(
        IDocumentStore store,
        RunnerDefinition runnerDefinition)
    {
        var steps = new List<RunnerInitStep>();

        if (runnerDefinition.InitStepRefs.Count > 0)
        {
            var definitions = (await store.Query<RunnerInitStepDefinition>().ToList())
                .ToDictionary(step => step.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var reference in runnerDefinition.InitStepRefs.OrderBy(r => r.Order))
            {
                if (string.IsNullOrWhiteSpace(reference.InitStepId))
                    continue;

                if (!definitions.TryGetValue(reference.InitStepId, out var definition))
                {
                    throw new InvalidOperationException(
                        $"Runner definition '{runnerDefinition.Name}' references missing init step '{reference.InitStepId}'.");
                }

                steps.Add(definition.ToInitStep(reference));
            }
        }

        steps.AddRange(runnerDefinition.InlineInitSteps.Select(RunnerDefinition.CloneInitStep));
        return steps;
    }
}
