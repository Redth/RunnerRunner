namespace RunnerRunner.Server.Tests.TestSupport;

internal sealed class TestClock
{
    public TestClock(DateTime? utcNow = null)
    {
        UtcNow = DateTime.SpecifyKind(
            utcNow ?? new DateTime(2026, 05, 18, 12, 00, 00, DateTimeKind.Utc),
            DateTimeKind.Utc);
    }

    public DateTime UtcNow { get; private set; }

    public DateTimeOffset UtcNowOffset => new(UtcNow, TimeSpan.Zero);

    public DateTime Advance(TimeSpan amount)
    {
        UtcNow = UtcNow.Add(amount);
        return UtcNow;
    }
}
