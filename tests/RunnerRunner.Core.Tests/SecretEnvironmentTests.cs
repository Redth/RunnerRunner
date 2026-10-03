using RunnerRunner.Core;

namespace RunnerRunner.Core.Tests;

public class SecretEnvironmentTests
{
    [Fact]
    public void Declare_RecordsDeclaredKeysThatHaveValues()
    {
        var vars = new Dictionary<string, string>
        {
            ["ASC_KEY_CONTENT"] = "-----BEGIN PRIVATE KEY-----",
            ["ASC_KEY_ID"] = "ABCD1234",
            ["PUBLIC_THING"] = "not-a-secret"
        };

        SecretEnvironment.Declare(vars, ["ASC_KEY_CONTENT", "ASC_KEY_ID"]);

        Assert.Equal("ASC_KEY_CONTENT,ASC_KEY_ID", vars[SecretEnvironment.SecretKeysVariable]);
    }

    [Fact]
    public void Declare_AlwaysIncludesCredentialTokens_EvenWhenNotMarkedByAnOperator()
    {
        // These are injected from the provider credential, so no operator ever
        // gets the chance to tick "secret" on them.
        var vars = new Dictionary<string, string> { ["RR_GITHUB_TOKEN"] = "ghp_abcdefghijklmnop" };

        SecretEnvironment.Declare(vars, []);

        Assert.Contains("RR_GITHUB_TOKEN", SecretEnvironment.Declared(vars));
    }

    [Fact]
    public void Declare_OmitsKeysThatHaveNoValue()
    {
        var vars = new Dictionary<string, string> { ["PRESENT"] = "value-here" };

        SecretEnvironment.Declare(vars, ["PRESENT", "ABSENT"]);

        Assert.Equal(["PRESENT"], SecretEnvironment.Declared(vars));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("abc")]
    public void Declare_OmitsValuesTooShortToMaskSafely(string value)
    {
        // Masking a 1-3 character value would redact unrelated text across the
        // whole log, and providers ignore such masks anyway.
        var vars = new Dictionary<string, string> { ["SHORT"] = value };

        SecretEnvironment.Declare(vars, ["SHORT"]);

        Assert.Empty(SecretEnvironment.Declared(vars));
    }

    [Fact]
    public void Declare_RemovesStaleDeclaration_WhenNothingIsSecret()
    {
        var vars = new Dictionary<string, string>
        {
            [SecretEnvironment.SecretKeysVariable] = "LEFTOVER",
            ["PUBLIC_THING"] = "not-a-secret"
        };

        SecretEnvironment.Declare(vars, []);

        Assert.False(vars.ContainsKey(SecretEnvironment.SecretKeysVariable));
    }

    [Fact]
    public void Declare_NeverRecordsTheDeclarationVariableItself()
    {
        var vars = new Dictionary<string, string>
        {
            [SecretEnvironment.SecretKeysVariable] = "something",
            ["REAL"] = "real-value"
        };

        SecretEnvironment.Declare(vars, [SecretEnvironment.SecretKeysVariable, "REAL"]);

        Assert.Equal(["REAL"], SecretEnvironment.Declared(vars));
    }

    [Fact]
    public void Declare_RejectsNamesThatWouldNotSurviveTheCommaDelimitedRoundTrip()
    {
        var vars = new Dictionary<string, string>
        {
            ["BAD,NAME"] = "value-here",
            ["BAD NAME"] = "value-here",
            ["GOOD_NAME"] = "value-here"
        };

        SecretEnvironment.Declare(vars, ["BAD,NAME", "BAD NAME", "GOOD_NAME"]);

        Assert.Equal(["GOOD_NAME"], SecretEnvironment.Declared(vars));
    }

    [Fact]
    public void Declare_RecordsNamesOnly_NeverValues()
    {
        var vars = new Dictionary<string, string> { ["TOKEN"] = "super-secret-value" };

        SecretEnvironment.Declare(vars, ["TOKEN"]);

        Assert.DoesNotContain("super-secret-value", vars[SecretEnvironment.SecretKeysVariable]);
    }

    [Fact]
    public void Declared_IsEmpty_ForAnEnvironmentWithNoDeclaration()
    {
        Assert.Empty(SecretEnvironment.Declared(new Dictionary<string, string>()));
        Assert.False(SecretEnvironment.HasSecrets(new Dictionary<string, string>()));
    }
}
