namespace RunnerRunner.Core;

/// <summary>
/// Declares which composed environment variables hold secret values, so the
/// job-started hook can register them with the provider's log masker before
/// the first user step runs.
///
/// The declaration travels as an ordinary environment variable rather than a
/// new field on the start-runner contract: agents are deployed per host and
/// upgrade independently, so an agent that predates masking must be able to
/// ignore this safely rather than fail to deserialize.
/// </summary>
public static class SecretEnvironment
{
    /// <summary>
    /// Holds a comma-separated list of variable <em>names</em> (never values)
    /// whose contents must be masked.
    /// </summary>
    public const string SecretKeysVariable = "RR_SECRET_KEYS";

    /// <summary>
    /// Credential-derived variables that are always secret. These are injected
    /// automatically from the provider credential and never appear in an
    /// <see cref="Models.EnvironmentVariableSet"/>, so an operator has no
    /// opportunity to mark them.
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysSecret =
    [
        "RR_GITHUB_TOKEN",
        "RR_GITEA_RUNNER_TOKEN",
        "RR_AZDO_PAT"
    ];

    /// <summary>
    /// Records the maskable subset of <paramref name="declaredSecretKeys"/> and
    /// <see cref="AlwaysSecret"/> into <paramref name="vars"/>.
    ///
    /// Call this <em>after</em> all composition layers and reference expansion,
    /// so the recorded names map to the values the job will actually receive.
    /// Masking is applied by value, so a variable that merely embeds a secret
    /// (for example a URL carrying a token) is redacted automatically once the
    /// secret itself is registered and need not be declared separately.
    /// </summary>
    public static void Declare(Dictionary<string, string> vars, IEnumerable<string> declaredSecretKeys)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(declaredSecretKeys);

        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in declaredSecretKeys.Concat(AlwaysSecret))
        {
            if (!IsUsableName(key))
                continue;

            if (vars.TryGetValue(key, out var value) && IsMaskable(value))
                names.Add(key);
        }

        if (names.Count == 0)
        {
            vars.Remove(SecretKeysVariable);
            return;
        }

        vars[SecretKeysVariable] = string.Join(',', names);
    }

    /// <summary>
    /// Reads the declared names back out of a composed environment.
    /// </summary>
    public static IReadOnlyList<string> Declared(IReadOnlyDictionary<string, string> vars)
    {
        if (vars is null || !vars.TryGetValue(SecretKeysVariable, out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>True when a composed environment carries anything to mask.</summary>
    public static bool HasSecrets(IReadOnlyDictionary<string, string> vars) => Declared(vars).Count > 0;

    /// <summary>
    /// A value is worth masking only if it is long enough to be a credential.
    /// Providers ignore masks shorter than three characters, and masking a very
    /// short value would redact unrelated text throughout the log.
    /// </summary>
    public static bool IsMaskable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 4;

    /// <summary>
    /// The name list is comma-delimited and is re-read by a shell, so reject
    /// anything that would not survive the round trip. Legal environment
    /// variable names are unaffected.
    /// </summary>
    private static bool IsUsableName(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key != SecretKeysVariable
        && !key.Contains(',')
        && !key.Any(char.IsWhiteSpace);
}
