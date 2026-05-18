namespace RunnerRunner.Server.Tests.TestSupport;

internal static class OrleansTestIds
{
    public static string Create(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
