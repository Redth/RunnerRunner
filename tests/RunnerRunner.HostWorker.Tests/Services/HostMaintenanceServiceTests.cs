using Microsoft.Extensions.Configuration;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

/// <summary>
/// Guards the age check that decides whether a cache directory gets recursively deleted.
/// Getting this wrong deletes a live build's working tree, so it is exercised against a real
/// filesystem rather than a stub.
/// </summary>
public class HostMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "rr-maint-" + Guid.NewGuid().ToString("n"));

    public HostMaintenanceServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string MakeTree(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void BackdateTree(string path, DateTime timestampUtc)
    {
        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(dir, timestampUtc);

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, timestampUtc);

        Directory.SetLastWriteTimeUtc(path, timestampUtc);
    }

    [Fact]
    public void EffectiveLastWriteUtc_WhenOnlyADeepFileIsRecent_ReportsTheEntryAsRecent()
    {
        // Xcode reuses DerivedData/<Project>/Build/Intermediates.noindex/... across builds, so a
        // running build can write several levels down without touching <Project> or its immediate
        // children. A one-level check would call this stale and delete it mid-build.
        var project = MakeTree("DerivedData", "MyApp-abc123");
        var deep = MakeTree("DerivedData", "MyApp-abc123", "Build", "Intermediates.noindex", "MyApp.build");

        var old = DateTime.UtcNow.AddDays(-30);
        BackdateTree(project, old);

        File.WriteAllText(Path.Combine(deep, "live.o"), "fresh");

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var effective = HostMaintenanceService.EffectiveLastWriteUtc(
            new DirectoryInfo(project),
            cutoff,
            maxDepth: 4);

        Assert.True(effective > cutoff, "A directory with recent deep writes must not look expired");
    }

    [Fact]
    public void EffectiveLastWriteUtc_WhenEverythingIsOld_ReportsTheEntryAsExpired()
    {
        var project = MakeTree("Archives", "2026-08-01", "MyApp.xcarchive", "dSYMs");
        File.WriteAllText(Path.Combine(project, "MyApp.dSYM"), "old");

        var entry = Path.Combine(_root, "Archives", "2026-08-01");
        BackdateTree(entry, DateTime.UtcNow.AddDays(-30));

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var effective = HostMaintenanceService.EffectiveLastWriteUtc(
            new DirectoryInfo(entry),
            cutoff,
            maxDepth: 4);

        Assert.True(effective < cutoff, "A fully stale tree should be eligible for pruning");
    }

    [Fact]
    public void EffectiveLastWriteUtc_StopsDescendingAtTheDepthLimit()
    {
        var shallow = MakeTree("Deep");
        var deep = MakeTree("Deep", "a", "b", "c", "d", "e");

        BackdateTree(shallow, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(Path.Combine(deep, "recent.txt"), "fresh");

        var cutoff = DateTime.UtcNow.AddDays(-7);

        // Depth 1 cannot see the recent file; depth 6 can. This documents that the bound is a
        // real trade-off, which is why the default is generous enough for Xcode's layout.
        Assert.True(HostMaintenanceService.EffectiveLastWriteUtc(new DirectoryInfo(shallow), cutoff, 1) < cutoff);
        Assert.True(HostMaintenanceService.EffectiveLastWriteUtc(new DirectoryInfo(shallow), cutoff, 6) > cutoff);
    }

    [Fact]
    public void EffectiveLastWriteUtc_ForAFile_UsesItsOwnTimestamp()
    {
        var file = Path.Combine(_root, "loose.txt");
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30));

        var cutoff = DateTime.UtcNow.AddDays(-7);

        Assert.True(HostMaintenanceService.EffectiveLastWriteUtc(new FileInfo(file), cutoff, 4) < cutoff);
    }

    [Fact]
    public void CacheRoots_BindFromNumberedEnvironmentVariableKeys()
    {
        // M4ini is Clancey's daily driver and overrides the cache roots through its LaunchAgent
        // plist so its signed Xcode Archives survive. If numbered env keys did not bind, the
        // override would silently fall back to the defaults and delete those archives.
        var environment = new Dictionary<string, string?>
        {
            ["HostWorker__Maintenance__CacheRoots__0"] = "/Users/clancey/Library/Developer/Xcode/DerivedData",
            ["HostWorker__Maintenance__CacheRoots__1"] = "/Users/clancey/Library/Caches/godot"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(environment.ToDictionary(
                kv => kv.Key.Replace("__", ":"),
                kv => kv.Value))
            .Build();

        var roots = configuration.GetSection("HostWorker:Maintenance:CacheRoots").Get<string[]>();

        Assert.NotNull(roots);
        Assert.Equal(2, roots!.Length);
        Assert.DoesNotContain(roots, r => r.Contains("Archives", StringComparison.OrdinalIgnoreCase));
    }
}
