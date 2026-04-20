using RunnerRunner.Server.Webhooks;

namespace RunnerRunner.Server.Tests.Webhooks;

public class WebhookLabelParserTests
{
    [Fact]
    public void Extract_Null_ReturnsEmpty()
    {
        var r = WebhookLabelParser.Extract(null);
        Assert.Empty(r.CleanLabels);
        Assert.Null(r.ImageTagOverride);
        Assert.Null(r.ImageTagOverrideRejectedReason);
    }

    [Fact]
    public void Extract_NoMagic_PreservesLabels()
    {
        var r = WebhookLabelParser.Extract(["self-hosted", "linux", "my-profile"]);
        Assert.Equal(["self-hosted", "linux", "my-profile"], r.CleanLabels);
        Assert.Null(r.ImageTagOverride);
        Assert.Null(r.ImageTagOverrideRejectedReason);
    }

    [Fact]
    public void Extract_ValidImageTag_StripsAndReturns()
    {
        var r = WebhookLabelParser.Extract(["self-hosted", "rr-image-tag=2025.11.07-abc123", "my-profile"]);
        Assert.Equal(["self-hosted", "my-profile"], r.CleanLabels);
        Assert.Equal("2025.11.07-abc123", r.ImageTagOverride);
        Assert.Null(r.ImageTagOverrideRejectedReason);
    }

    [Fact]
    public void Extract_CaseInsensitiveKey()
    {
        var r = WebhookLabelParser.Extract(["RR-Image-Tag=v1.2.3"]);
        Assert.Equal("v1.2.3", r.ImageTagOverride);
        Assert.Empty(r.CleanLabels);
    }

    [Fact]
    public void Extract_InvalidTag_RejectsWithReason()
    {
        var r = WebhookLabelParser.Extract(["rr-image-tag=bad tag with spaces"]);
        Assert.Empty(r.CleanLabels);
        Assert.Null(r.ImageTagOverride);
        Assert.NotNull(r.ImageTagOverrideRejectedReason);
        Assert.Contains("Invalid image tag", r.ImageTagOverrideRejectedReason);
    }

    [Fact]
    public void Extract_EmptyValue_Rejected()
    {
        var r = WebhookLabelParser.Extract(["rr-image-tag="]);
        Assert.Empty(r.CleanLabels);
        Assert.Null(r.ImageTagOverride);
        Assert.NotNull(r.ImageTagOverrideRejectedReason);
    }

    [Fact]
    public void Extract_MultipleValidTags_LastWins()
    {
        var r = WebhookLabelParser.Extract(["rr-image-tag=v1", "rr-image-tag=v2"]);
        Assert.Equal("v2", r.ImageTagOverride);
        Assert.Empty(r.CleanLabels);
    }

    [Fact]
    public void Extract_UnrecognizedRrLabel_Retained()
    {
        var r = WebhookLabelParser.Extract(["rr-some-future-label=foo", "self-hosted"]);
        Assert.Equal(["rr-some-future-label=foo", "self-hosted"], r.CleanLabels);
        Assert.Null(r.ImageTagOverride);
    }

    [Fact]
    public void Extract_TrimsWhitespace()
    {
        var r = WebhookLabelParser.Extract(["  self-hosted  ", "  rr-image-tag=v9  "]);
        Assert.Equal(["self-hosted"], r.CleanLabels);
        Assert.Equal("v9", r.ImageTagOverride);
    }
}
