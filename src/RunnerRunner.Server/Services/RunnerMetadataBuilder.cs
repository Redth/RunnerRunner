using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Builds the informational <c>rr-*</c> labels and <c>RR_META_*</c>
/// environment variables that describe a runner deployment. These show
/// up in GitHub Actions' "Set up job" block (labels) and in the job-started
/// hook banner (env vars).
/// </summary>
public static class RunnerMetadataBuilder
{
    // Label prefix used for every RunnerRunner-emitted metadata label so
    // users can easily spot / filter them. Must stay stable — downstream
    // tooling grep for it.
    public const string LabelPrefix = "rr-";

    /// <summary>
    /// Produces the RR_META_* env vars describing this runner deployment.
    /// Values missing from the profile/host are simply omitted.
    /// </summary>
    public static Dictionary<string, string> BuildMetadataEnv(
        RunnerProfile profile,
        Host? host,
        string? runnerAgentVersion,
        string? instanceId,
        string? imageTagOverride = null)
    {
        var env = new Dictionary<string, string>();
        Set(env, "RR_META_BACKEND", profile.ExecutionBackend.ToString().ToLowerInvariant());
        Set(env, "RR_META_PROVIDER", profile.Provider.ToString());
        Set(env, "RR_META_PROFILE", profile.Name);
        Set(env, "RR_META_HOST", host?.Label);
        Set(env, "RR_META_AGENT_VERSION", runnerAgentVersion);
        Set(env, "RR_META_INSTANCE_ID", instanceId);

        var (imageRef, imageTag) = ExtractImageRef(profile);
        Set(env, "RR_META_IMAGE", imageRef);
        Set(env, "RR_META_TAG", string.IsNullOrWhiteSpace(imageTagOverride) ? imageTag : imageTagOverride);

        return env;
    }

    /// <summary>
    /// Produces sanitized <c>rr-*</c> metadata labels derived from the profile,
    /// host, and image configuration. All labels obey GitHub's label rules:
    /// only alphanumerics, <c>-</c>, <c>_</c>, <c>.</c>; length capped at 256.
    /// </summary>
    public static List<string> BuildMetadataLabels(RunnerProfile profile, Host? host)
    {
        var labels = new List<string>();

        void Add(string suffix, string? value)
        {
            var sanitized = SanitizeLabel(value);
            if (!string.IsNullOrEmpty(sanitized))
                labels.Add($"{LabelPrefix}{suffix}:{sanitized}");
        }

        Add("backend", profile.ExecutionBackend.ToString().ToLowerInvariant());
        Add("provider", profile.Provider.ToString());
        Add("profile", profile.Name);
        Add("host", host?.Label);

        var (imageRef, imageTag) = ExtractImageRef(profile);
        if (!string.IsNullOrWhiteSpace(imageRef))
        {
            // Image refs include slashes and dots which aren't label-legal;
            // fold them to dashes via SanitizeLabel.
            Add("image", imageRef);
            if (!string.IsNullOrWhiteSpace(imageTag))
                Add("tag", imageTag);
        }

        return labels;
    }

    /// <summary>
    /// Merges metadata labels into <paramref name="existing"/> (case-insensitive
    /// de-duplication). If <see cref="RunnerProfile.EmitMetadataLabels"/> is
    /// false, <paramref name="existing"/> is returned untouched.
    /// </summary>
    public static List<string> MergeMetadataLabels(
        List<string> existing,
        RunnerProfile profile,
        Host? host)
    {
        if (!profile.EmitMetadataLabels)
            return existing;

        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(existing);

        foreach (var label in BuildMetadataLabels(profile, host))
        {
            if (seen.Add(label))
                result.Add(label);
        }

        return result;
    }

    /// <summary>
    /// Sanitizes <paramref name="value"/> to conform to GitHub's runner label
    /// rules: keep alphanumerics + <c>-</c>, <c>_</c>, <c>.</c>; replace other
    /// chars with <c>-</c>; collapse consecutive dashes; trim to 256 chars.
    /// Returns <c>null</c> for empty/whitespace input.
    /// Unicode letters/digits are allowed here — GitHub's label rules permit them.
    /// For names that become Docker container names, Tart VM names, or file-system
    /// paths downstream, use <see cref="SanitizeRunnerNameComponent"/> instead, which
    /// is ASCII-only.
    /// </summary>
    public static string? SanitizeLabel(string? value)
        => Sanitize(value, static c => char.IsLetterOrDigit(c));

    /// <summary>
    /// Sanitizes <paramref name="value"/> for use as a runner-name component that
    /// becomes a Docker container name (<c>[a-zA-Z0-9][a-zA-Z0-9_.-]*</c>), Tart VM
    /// name, or file-system path segment downstream. ASCII-only — ASCII digits/letters
    /// plus <c>-</c>, <c>_</c>, <c>.</c>; everything else (including non-ASCII letters,
    /// which <see cref="SanitizeLabel"/> would keep) collapses to <c>-</c>. Returns
    /// <c>null</c> for empty/whitespace input, or input that sanitizes to nothing.
    /// </summary>
    public static string? SanitizeRunnerNameComponent(string? value)
        => Sanitize(value, static c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'));

    private static string? Sanitize(string? value, Func<char, bool> isAllowedAlnum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var chars = new char[value.Length];
        var write = 0;
        var lastDash = false;

        foreach (var c in value)
        {
            if (isAllowedAlnum(c) || c == '-' || c == '_' || c == '.')
            {
                chars[write++] = c;
                lastDash = c == '-';
            }
            else if (!lastDash)
            {
                chars[write++] = '-';
                lastDash = true;
            }
        }

        var sanitized = new string(chars, 0, write).Trim('-', '.', '_');
        if (sanitized.Length == 0)
            return null;

        return sanitized.Length > 256 ? sanitized[..256] : sanitized;
    }

    private static (string? Ref, string? Tag) ExtractImageRef(RunnerProfile profile)
    {
        if (profile.DockerConfig is { } docker)
        {
            var repo = ImageReference.BuildRepository(docker.RegistryUrl, docker.ImageName);
            return (string.IsNullOrWhiteSpace(repo) ? null : repo, string.IsNullOrWhiteSpace(docker.Tag) ? null : docker.Tag);
        }

        if (profile.TartConfig is { } tart)
        {
            var repo = ImageReference.BuildRepository(tart.RegistryUrl, tart.ImageName);
            return (string.IsNullOrWhiteSpace(repo) ? null : repo, string.IsNullOrWhiteSpace(tart.Tag) ? null : tart.Tag);
        }

        return (null, null);
    }

    private static void Set(Dictionary<string, string> env, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            env[key] = value;
    }
}
