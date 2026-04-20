using System.Text.RegularExpressions;

namespace RunnerRunner.Server.Webhooks;

/// <summary>
/// Extracts "magic" <c>rr-*</c> labels from a webhook job's <c>runs-on</c>
/// label set. Today only <c>rr-image-tag=&lt;value&gt;</c> is recognized.
///
/// Magic labels are always stripped from the returned <see cref="MagicLabelResult.CleanLabels"/>
/// so they don't leak into <see cref="RunnerRunner.Core.Models.ProvisioningRule"/>
/// label-mapping comparisons (which use exact-string matching) or onto the
/// final runner's label surface. The raw, unfiltered labels still live on
/// the persisted <see cref="RunnerRunner.Core.Models.WebhookEvent"/> audit
/// record so operators can see exactly what the caller sent.
/// </summary>
public static class WebhookLabelParser
{
    // Must be recognized regardless of case; GitHub lower-cases labels but
    // Gitea and custom webhooks may not.
    private const string ImageTagKey = "rr-image-tag";

    // Docker tag rules: first char alphanumeric; remaining chars alphanumeric,
    // underscore, hyphen, or period; max 128 chars.
    private static readonly Regex TagRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record MagicLabelResult(
        List<string> CleanLabels,
        string? ImageTagOverride,
        string? ImageTagOverrideRejectedReason);

    /// <summary>
    /// Splits <paramref name="labels"/> into (clean, magic). The clean list
    /// preserves original order minus any <c>rr-*</c> label that was recognized
    /// as magic. Unrecognized <c>rr-*</c> labels are **retained** in the clean
    /// list — we only strip labels we know about so older UI/CI behavior is
    /// preserved for existing <c>rr-*</c> label conventions.
    /// </summary>
    public static MagicLabelResult Extract(IEnumerable<string>? labels)
    {
        var clean = new List<string>();
        string? tag = null;
        string? rejected = null;

        if (labels == null)
            return new MagicLabelResult(clean, null, null);

        foreach (var raw in labels)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var label = raw.Trim();
            if (!TryParseImageTag(label, out var parsedTag, out var rejectReason))
            {
                clean.Add(label);
                continue;
            }

            // Magic label recognized — always strip from clean list so it
            // can't affect profile matching or land on the runner.
            if (rejectReason != null)
            {
                // First rejection wins so operators see the original cause.
                rejected ??= rejectReason;
                continue;
            }

            // Multiple valid image-tag labels: last one wins (matches Docker
            // CLI semantics where a later --tag would override an earlier one).
            tag = parsedTag;
        }

        return new MagicLabelResult(clean, tag, rejected);
    }

    private static bool TryParseImageTag(string label, out string? tag, out string? rejectedReason)
    {
        tag = null;
        rejectedReason = null;

        var eq = label.IndexOf('=');
        if (eq <= 0)
            return false;

        var key = label[..eq];
        if (!key.Equals(ImageTagKey, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = label[(eq + 1)..].Trim();
        if (value.Length == 0)
        {
            rejectedReason = $"Empty value for '{ImageTagKey}=' label";
            return true;
        }

        if (!TagRegex.IsMatch(value))
        {
            rejectedReason =
                $"Invalid image tag '{value}' — must match {TagRegex} (Docker tag rules)";
            return true;
        }

        tag = value;
        return true;
    }
}
