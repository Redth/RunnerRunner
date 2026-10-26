using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Tests;

public class ImageReferenceTests
{
    [Fact]
    public void DigestOverridesDefaultTagWithoutBecomingPartOfTheRepositoryName()
    {
        var image = "example-org/runner@sha256:" + new string('a', 64);
        var expected = "ghcr.io/" + image;
        Assert.Equal(expected, ImageReference.Build("https://ghcr.io", image, "latest"));
        Assert.Equal(expected, ImageReference.Build("ghcr.io", expected, "latest"));
    }

    [Theory]
    [InlineData("registry:5000", "team/image:v2", "v1", "registry:5000/team/image:v2")]
    [InlineData("ghcr.io", "team/image", "v1", "ghcr.io/team/image:v1")]
    public void PullAndCreateUseTheSameReference(string registry, string image, string tag, string expected)
    {
        Assert.Equal(expected, ImageReference.Build(registry, image, tag));
    }

    [Theory]
    [InlineData("sha256", 64)]
    [InlineData("sha512", 128)]
    public void PreservesDigestAlgorithmAndOptionalTag(string algorithm, int length)
    {
        var image = "team/runner:v2@" + algorithm + ":" + new string('a', length);
        Assert.Equal("registry:5000/" + image, ImageReference.Build("registry:5000", image, "latest"));
    }
}
