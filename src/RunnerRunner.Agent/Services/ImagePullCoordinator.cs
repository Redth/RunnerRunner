using System.Collections.Concurrent;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Coordinates image-pull operations on this agent so concurrent requests
/// for the same image share a single in-flight pull instead of starting
/// duplicate downloads. This is a per-agent-process coordinator — each host
/// has its own container/VM daemon, so cross-host coordination is not needed.
///
/// Callers register a "pull factory" (a delegate that performs the actual
/// pull). The first caller for a given key starts the pull; subsequent
/// callers attach to the same Task. The entry is removed once the pull
/// completes (success or failure), allowing retries.
///
/// Caller cancellation: each caller's awaited Task respects their own
/// CancellationToken via WaitAsync. The underlying pull, however, runs
/// to completion regardless — cancelling one caller does NOT abort the
/// shared pull, which is what other waiters want. This trades some wasted
/// work on a cancelled-then-abandoned pull for predictable behaviour when
/// many jobs are queued behind the same image.
/// </summary>
public sealed class ImagePullCoordinator
{
    private readonly ILogger<ImagePullCoordinator> _logger;
    private readonly ConcurrentDictionary<string, InflightPull> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public ImagePullCoordinator(ILogger<ImagePullCoordinator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensures a pull is in flight for <paramref name="key"/> and waits for it
    /// to complete. If a pull is already running, this returns the existing
    /// task; otherwise it starts a new one using <paramref name="pullFactory"/>.
    /// </summary>
    /// <param name="key">Image identity (e.g. "docker:registry/image:tag").</param>
    /// <param name="pullFactory">Delegate that performs the actual pull.</param>
    /// <param name="ct">Caller cancellation. Stops the awaiting only — the
    /// underlying pull runs to completion.</param>
    public async Task PullOnceAsync(string key, Func<Task> pullFactory, CancellationToken ct = default)
    {
        var inflight = _inflight.GetOrAdd(key, k =>
        {
            _logger.LogInformation("Starting coordinated image pull: {Key}", k);
            return new InflightPull(StartPull(k, pullFactory));
        });

        inflight.IncrementWaiter();
        try
        {
            await inflight.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            inflight.DecrementWaiter();
        }
    }

    /// <summary>
    /// Returns true when a pull for <paramref name="key"/> is currently in flight.
    /// </summary>
    public bool IsPulling(string key) => _inflight.ContainsKey(key);

    /// <summary>
    /// Snapshot of the keys currently being pulled, primarily for diagnostics.
    /// </summary>
    public IReadOnlyCollection<string> InflightKeys => _inflight.Keys.ToArray();

    private Task StartPull(string key, Func<Task> pullFactory)
    {
        // Run the actual pull on a worker thread so the caller's continuation
        // (e.g., a SignalR hub callback) does not run inline on the dispatcher.
        return Task.Run(async () =>
        {
            try
            {
                await pullFactory().ConfigureAwait(false);
                _logger.LogInformation("Coordinated pull complete: {Key}", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Coordinated pull failed: {Key}", key);
                throw;
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        });
    }

    private sealed class InflightPull
    {
        private int _waiters;
        public Task Task { get; }
        public InflightPull(Task task) { Task = task; }
        public void IncrementWaiter() => Interlocked.Increment(ref _waiters);
        public void DecrementWaiter() => Interlocked.Decrement(ref _waiters);
        public int Waiters => Volatile.Read(ref _waiters);
    }
}
