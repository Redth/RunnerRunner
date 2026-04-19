using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class RunnerMetadataBuilderTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("simple", "simple")]
    [InlineData("has spaces", "has-spaces")]
    [InlineData("ghcr.io/acme/runner", "ghcr.io-acme-runner")]
    [InlineData("MANY   spaces", "MANY-spaces")]
    [InlineData("!!leading", "leading")]
    [InlineData("trailing---", "trailing")]
    [InlineData("weird$%chars^ok", "weird-chars-ok")]
    public void SanitizeLabel_Normalizes(string? input, string? expected)
    {
        var actual = RunnerMetadataBuilder.SanitizeLabel(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SanitizeLabel_TrimsTo256Chars()
    {
        var input = new string('a', 500);
        var actual = RunnerMetadataBuilder.SanitizeLabel(input);
        Assert.NotNull(actual);
        Assert.Equal(256, actual!.Length);
    }

    [Fact]
    public void BuildMetadataEnv_IncludesDockerImageBits()
    {
        var profile = new RunnerProfile
        {
            Name = "linux-large",
            Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Docker,
            DockerConfig = new DockerImageConfig
            {
                RegistryUrl = "ghcr.io",
                ImageName = "acme/runner",
                Tag = "v1.2.3"
            }
        };
        var host = new Host { Name = "host-1", DisplayName = "Host One" };

        var env = RunnerMetadataBuilder.BuildMetadataEnv(profile, host, "2.333.1", "inst-xyz");

        Assert.Equal("docker", env["RR_META_BACKEND"]);
        Assert.Equal("GitHubActions", env["RR_META_PROVIDER"]);
        Assert.Equal("linux-large", env["RR_META_PROFILE"]);
        Assert.Equal("Host One", env["RR_META_HOST"]);
        Assert.Equal("2.333.1", env["RR_META_AGENT_VERSION"]);
        Assert.Equal("inst-xyz", env["RR_META_INSTANCE_ID"]);
        Assert.Equal("ghcr.io/acme/runner", env["RR_META_IMAGE"]);
        Assert.Equal("v1.2.3", env["RR_META_TAG"]);
    }

    [Fact]
    public void BuildMetadataEnv_OmitsUnknownValues()
    {
        var profile = new RunnerProfile
        {
            Name = "native-only",
            Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Native
        };

        var env = RunnerMetadataBuilder.BuildMetadataEnv(profile, host: null, runnerAgentVersion: null, instanceId: null);

        Assert.Equal("native", env["RR_META_BACKEND"]);
        Assert.False(env.ContainsKey("RR_META_HOST"));
        Assert.False(env.ContainsKey("RR_META_IMAGE"));
        Assert.False(env.ContainsKey("RR_META_TAG"));
        Assert.False(env.ContainsKey("RR_META_AGENT_VERSION"));
        Assert.False(env.ContainsKey("RR_META_INSTANCE_ID"));
    }

    [Fact]
    public void BuildMetadataLabels_PrefixAllLabelsAndSanitize()
    {
        var profile = new RunnerProfile
        {
            Name = "ci builder",
            Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Tart,
            TartConfig = new TartImageConfig
            {
                RegistryUrl = "ghcr.io",
                ImageName = "acme/macos-runner",
                Tag = "sequoia"
            }
        };

        var labels = RunnerMetadataBuilder.BuildMetadataLabels(profile, new Host { Name = "mac-mini" });

        Assert.Contains("rr-backend:tart", labels);
        Assert.Contains("rr-profile:ci-builder", labels);
        Assert.Contains("rr-host:mac-mini", labels);
        Assert.Contains("rr-image:ghcr.io-acme-macos-runner", labels);
        Assert.Contains("rr-tag:sequoia", labels);
        Assert.All(labels, l => Assert.StartsWith("rr-", l));
    }

    [Fact]
    public void MergeMetadataLabels_NoOp_WhenProfileDisabled()
    {
        var profile = new RunnerProfile
        {
            Name = "p",
            ExecutionBackend = ExecutionBackend.Docker,
            EmitMetadataLabels = false
        };

        var merged = RunnerMetadataBuilder.MergeMetadataLabels(["self-hosted"], profile, host: null);

        Assert.Equal(["self-hosted"], merged);
    }

    [Fact]
    public void MergeMetadataLabels_DeDuplicates()
    {
        var profile = new RunnerProfile
        {
            Name = "p",
            ExecutionBackend = ExecutionBackend.Docker
        };

        var merged = RunnerMetadataBuilder.MergeMetadataLabels(
            ["self-hosted", "rr-backend:docker"], profile, host: null);

        Assert.Single(merged, "rr-backend:docker");
    }
}
