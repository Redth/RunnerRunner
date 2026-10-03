using RunnerRunner.Agent.Services;

namespace RunnerRunner.Agent.Tests.Services;

public class JobHookScriptBuilderTests
{
    [Fact]
    public void BuildBashScript_WrapsOutputInActionsGroup()
    {
        var script = JobHookScriptBuilder.BuildBashScript();

        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("::group::RunnerRunner environment", script);
        Assert.Contains("::endgroup::", script);
        Assert.Contains("RR_META_BACKEND", script);
        Assert.Contains("RR_META_IMAGE", script);
        Assert.Contains("RR_META_AGENT_VERSION", script);
    }

    [Fact]
    public void BuildPowerShellScript_WrapsOutputInActionsGroup()
    {
        var script = JobHookScriptBuilder.BuildPowerShellScript();

        Assert.Contains("::group::RunnerRunner environment", script);
        Assert.Contains("::endgroup::", script);
        Assert.Contains("$env:RR_META_BACKEND", script);
        Assert.Contains("$env:RR_META_IMAGE", script);
    }

    [Fact]
    public void WriteBashScript_CreatesExecutableFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rr-hook-test-{Guid.NewGuid():N}");
        try
        {
            var path = JobHookScriptBuilder.WriteBashScript(tempDir);

            Assert.True(File.Exists(path));
            Assert.Equal(JobHookScriptBuilder.BashFileName, Path.GetFileName(path));
            var content = File.ReadAllText(path);
            Assert.Contains("::group::RunnerRunner environment", content);

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute),
                    $"Expected bash script to be executable; got {mode}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void IsHookRequested_InterpretsSentinel(string value, bool expected)
    {
        var dict = new Dictionary<string, string> { [JobHookScriptBuilder.RequestedEnvVarName] = value };
        Assert.Equal(expected, JobHookScriptBuilder.IsHookRequested(dict));
    }

    [Fact]
    public void IsHookRequested_ReturnsFalse_WhenUnset()
    {
        Assert.False(JobHookScriptBuilder.IsHookRequested(new Dictionary<string, string>()));
    }

    [Fact]
    public void IsHookNeeded_IsTrueForSecrets_EvenWhenTheBannerWasNeverRequested()
    {
        // Masking is not opt-in. A profile carrying credentials needs the hook
        // installed whether or not anyone asked for the banner.
        var dict = new Dictionary<string, string>
        {
            [RunnerRunner.Core.SecretEnvironment.SecretKeysVariable] = "TOKEN"
        };

        Assert.False(JobHookScriptBuilder.IsHookRequested(dict));
        Assert.True(JobHookScriptBuilder.IsHookNeeded(dict));
    }

    [Fact]
    public void IsHookNeeded_IsFalse_WhenThereIsNeitherBannerNorSecret()
    {
        Assert.False(JobHookScriptBuilder.IsHookNeeded(new Dictionary<string, string>()));
    }

    // ---------------------------------------------------------------------
    // Execution tests. The masking logic is shell, so asserting on the script
    // text would pass even when the logic is wrong. These run it instead.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The bash hook is only written on non-Windows hosts, so these execution
    /// tests are inert on Windows. xunit 2.x has no dynamic skip.
    /// </summary>
    private static bool SupportsPosixHook => !OperatingSystem.IsWindows();

    private static (int ExitCode, string Stdout, string Stderr) RunBashHook(
        Dictionary<string, string> env)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rr-hook-run-{Guid.NewGuid():N}");
        try
        {
            var path = JobHookScriptBuilder.WriteBashScript(tempDir);

            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh", path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/usr/bin:/bin";
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;

            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            return (p.ExitCode, stdout, stderr);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BashHook_EmitsOneMaskPerLineOfAMultiLineSecret()
    {
        if (!SupportsPosixHook) return;

        // ::add-mask:: is line-oriented. A PEM registered as a single blob
        // masks nothing at all, which fails silently.
        const string pem = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\nhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----";

        var (exit, stdout, _) = RunBashHook(new Dictionary<string, string>
        {
            ["RR_SECRET_KEYS"] = "ASC_KEY_CONTENT",
            ["ASC_KEY_CONTENT"] = pem
        });

        Assert.Equal(0, exit);

        var masked = stdout.Split('\n')
            .Where(l => l.StartsWith("::add-mask::"))
            .Select(l => l["::add-mask::".Length..])
            .ToList();

        Assert.Equal(pem.Split('\n'), masked);
    }

    [Fact]
    public void BashHook_MasksEveryDeclaredKey()
    {
        if (!SupportsPosixHook) return;

        var (exit, stdout, _) = RunBashHook(new Dictionary<string, string>
        {
            ["RR_SECRET_KEYS"] = "KEYSTORE,TOKEN",
            ["KEYSTORE"] = "base64-keystore-blob",
            ["TOKEN"] = "ghp_abcdefghijklmnop"
        });

        Assert.Equal(0, exit);
        Assert.Contains("::add-mask::base64-keystore-blob", stdout);
        Assert.Contains("::add-mask::ghp_abcdefghijklmnop", stdout);
    }

    [Fact]
    public void BashHook_NeverPrintsASecretOutsideAMaskDirective()
    {
        if (!SupportsPosixHook) return;

        const string secret = "ghp_abcdefghijklmnop";

        var (exit, stdout, stderr) = RunBashHook(new Dictionary<string, string>
        {
            ["RR_SECRET_KEYS"] = "TOKEN",
            ["TOKEN"] = secret,
            ["RR_HOOK_JOB_STARTED_REQUESTED"] = "1",
            ["RR_META_BACKEND"] = "native"
        });

        Assert.Equal(0, exit);

        var leaked = stdout.Split('\n')
            .Where(l => l.Contains(secret) && !l.StartsWith("::add-mask::"))
            .ToList();

        Assert.Empty(leaked);
        Assert.DoesNotContain(secret, stderr);
    }

    [Fact]
    public void BashHook_SkipsTheBanner_WhenOnlyMaskingIsNeeded()
    {
        if (!SupportsPosixHook) return;

        var (exit, stdout, _) = RunBashHook(new Dictionary<string, string>
        {
            ["RR_SECRET_KEYS"] = "TOKEN",
            ["TOKEN"] = "ghp_abcdefghijklmnop"
        });

        Assert.Equal(0, exit);
        Assert.Contains("::add-mask::", stdout);
        Assert.DoesNotContain("::group::", stdout);
    }

    [Fact]
    public void BashHook_StillRendersTheBanner_WhenRequested()
    {
        if (!SupportsPosixHook) return;

        var (exit, stdout, _) = RunBashHook(new Dictionary<string, string>
        {
            ["RR_HOOK_JOB_STARTED_REQUESTED"] = "1",
            ["RR_META_BACKEND"] = "native",
            ["RR_META_HOST"] = "m4ini"
        });

        Assert.Equal(0, exit);
        Assert.Contains("::group::RunnerRunner environment", stdout);
        Assert.Contains("Backend:         native", stdout);
        Assert.Contains("::endgroup::", stdout);
    }

    [Theory]
    // The shell's banner decision must agree with IsHookRequested. The hook is
    // now installed for masking alone, so a profile that explicitly disabled
    // the banner must not start printing one as a side effect.
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void BashHook_BannerDecisionMatchesIsHookRequested(string sentinel, bool expectBanner)
    {
        if (!SupportsPosixHook) return;

        var dict = new Dictionary<string, string>
        {
            [JobHookScriptBuilder.RequestedEnvVarName] = sentinel
        };
        Assert.Equal(expectBanner, JobHookScriptBuilder.IsHookRequested(dict));

        var (exit, stdout, _) = RunBashHook(new Dictionary<string, string>
        {
            ["RR_SECRET_KEYS"] = "TOKEN",
            ["TOKEN"] = "ghp_abcdefghijklmnop",
            ["RR_HOOK_JOB_STARTED_REQUESTED"] = sentinel
        });

        Assert.Equal(0, exit);
        // Masking happens either way.
        Assert.Contains("::add-mask::ghp_abcdefghijklmnop", stdout);
        Assert.Equal(expectBanner, stdout.Contains("::group::"));
    }

    [Theory]
    // A hook that exits non-zero fails the job before its first step, so every
    // one of these degenerate inputs must still succeed.
    [InlineData("", "")]
    [InlineData("DECLARED_BUT_ABSENT", "")]
    [InlineData(",,,", "")]
    [InlineData("TOKEN", "ab")]
    public void BashHook_AlwaysExitsZero(string secretKeys, string tokenValue)
    {
        if (!SupportsPosixHook) return;

        var env = new Dictionary<string, string> { ["RR_SECRET_KEYS"] = secretKeys };
        if (!string.IsNullOrEmpty(tokenValue))
            env["TOKEN"] = tokenValue;

        var (exit, _, stderr) = RunBashHook(env);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.Trim());
    }
}
