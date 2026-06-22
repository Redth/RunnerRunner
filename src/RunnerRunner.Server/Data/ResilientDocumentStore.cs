using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Data;

internal sealed class ResilientDocumentStore : IDocumentStore, IDisposable
{
    private readonly DocumentStoreOptions _options;
    private readonly ILogger<ResilientDocumentStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DocumentStore _store;

    public ResilientDocumentStore(DocumentStoreOptions options, ILogger<ResilientDocumentStore> logger)
    {
        _options = options;
        _logger = logger;
        _store = new DocumentStore(options);
    }

    public IDocumentQuery<T> Query<T>(JsonTypeInfo<T>? jsonTypeInfo = null) where T : class
        => new ResilientDocumentQuery<T>(this, store => store.Query(jsonTypeInfo));

    public Task Insert<T>(T document, JsonTypeInfo<T>? jsonTypeInfo = null, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Insert(document, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task<int> BatchInsert<T>(IEnumerable<T> documents, JsonTypeInfo<T>? jsonTypeInfo = null, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.BatchInsert(documents, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task Update<T>(T document, JsonTypeInfo<T>? jsonTypeInfo = null, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Update(document, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task Upsert<T>(T patch, JsonTypeInfo<T>? jsonTypeInfo = null, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Upsert(patch, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task<bool> SetProperty<T>(
        object id,
        Expression<Func<T, object>> property,
        object? value,
        JsonTypeInfo<T>? jsonTypeInfo = null,
        CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.SetProperty(id, property, value, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task<bool> RemoveProperty<T>(
        object id,
        Expression<Func<T, object>> property,
        JsonTypeInfo<T>? jsonTypeInfo = null,
        CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.RemoveProperty(id, property, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task<T?> Get<T>(object id, JsonTypeInfo<T>? jsonTypeInfo = null, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Get(id, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task<JsonPatchDocument<T>?> GetDiff<T>(
        object id,
        T modified,
        JsonTypeInfo<T>? jsonTypeInfo = null,
        CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.GetDiff(id, modified, jsonTypeInfo, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<T>> Query<T>(
        string whereClause,
        JsonTypeInfo<T>? jsonTypeInfo = null,
        object? parameters = null,
        CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Query(whereClause, jsonTypeInfo, parameters, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<T> QueryStream<T>(
        string whereClause,
        JsonTypeInfo<T>? jsonTypeInfo = null,
        object? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        var items = await ExecuteWithRetryAsync(async store =>
        {
            var results = new List<T>();
            await foreach (var item in store.QueryStream(whereClause, jsonTypeInfo, parameters, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                results.Add(item);
            }

            return (IReadOnlyList<T>)results;
        }, cancellationToken);

        foreach (var item in items)
            yield return item;
    }

    public Task<int> Count<T>(string? whereClause = null, object? parameters = null, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Count<T>(whereClause, parameters, cancellationToken), cancellationToken);

    public Task<bool> Remove<T>(object id, CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Remove<T>(id, cancellationToken), cancellationToken);

    public Task<int> Clear<T>(CancellationToken cancellationToken = default) where T : class
        => ExecuteWithRetryAsync(store => store.Clear<T>(cancellationToken), cancellationToken);

    public Task RunInTransaction(Func<IDocumentStore, Task> operation, CancellationToken cancellationToken = default)
        => ExecuteWithRetryAsync(store => store.RunInTransaction(operation, cancellationToken), cancellationToken);

    public Task Backup(string destinationPath, CancellationToken cancellationToken = default)
        => ExecuteWithRetryAsync(store => store.Backup(destinationPath, cancellationToken), cancellationToken);

    internal static bool IsClosedConnectionError(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains("Connection is not open", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ExecuteWithRetryAsync(Func<IDocumentStore, Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async store =>
        {
            await operation(store);
            return true;
        }, cancellationToken);
    }

    private async Task<TResult> ExecuteWithRetryAsync<TResult>(
        Func<IDocumentStore, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var retried = false;

        while (true)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await operation(_store);
            }
            catch (Exception ex) when (!retried && IsClosedConnectionError(ex))
            {
                retried = true;
                ResetStore();
                _logger.LogWarning(ex, "Document store PostgreSQL connection was closed; recreated the store and retrying the operation.");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void ResetStore()
    {
        var oldStore = _store;
        _store = new DocumentStore(_options);
        oldStore.Dispose();
    }

    public void Dispose()
    {
        _store.Dispose();
        _gate.Dispose();
    }

    private sealed class ResilientDocumentQuery<T>(
        ResilientDocumentStore owner,
        Func<IDocumentStore, IDocumentQuery<T>> buildQuery) : IDocumentQuery<T> where T : class
    {
        public IDocumentQuery<T> Where(Expression<Func<T, bool>> predicate)
            => new ResilientDocumentQuery<T>(owner, store => buildQuery(store).Where(predicate));

        public IDocumentQuery<T> OrderBy(Expression<Func<T, object>> selector)
            => new ResilientDocumentQuery<T>(owner, store => buildQuery(store).OrderBy(selector));

        public IDocumentQuery<T> OrderByDescending(Expression<Func<T, object>> selector)
            => new ResilientDocumentQuery<T>(owner, store => buildQuery(store).OrderByDescending(selector));

        public IDocumentQuery<T> GroupBy(Expression<Func<T, object>> selector)
            => new ResilientDocumentQuery<T>(owner, store => buildQuery(store).GroupBy(selector));

        public IDocumentQuery<T> Paginate(int offset, int take)
            => new ResilientDocumentQuery<T>(owner, store => buildQuery(store).Paginate(offset, take));

        public IDocumentQuery<TResult> Select<TResult>(
            Expression<Func<T, TResult>> selector,
            JsonTypeInfo<TResult>? resultTypeInfo = null) where TResult : class
            => new ResilientDocumentQuery<TResult>(owner, store => buildQuery(store).Select(selector, resultTypeInfo));

        public Task<IReadOnlyList<T>> ToList(CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).ToList(ct), ct);

        public async IAsyncEnumerable<T> ToAsyncEnumerable([EnumeratorCancellation] CancellationToken ct = default)
        {
            var items = await ToList(ct);
            foreach (var item in items)
                yield return item;
        }

        public Task<long> Count(CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).Count(ct), ct);

        public Task<bool> Any(CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).Any(ct), ct);

        public Task<int> ExecuteDelete(CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).ExecuteDelete(ct), ct);

        public Task<int> ExecuteUpdate(Expression<Func<T, object>> property, object? value, CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).ExecuteUpdate(property, value, ct), ct);

        public Task<TValue> Max<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).Max(selector, ct), ct);

        public Task<TValue> Min<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).Min(selector, ct), ct);

        public Task<TValue> Sum<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).Sum(selector, ct), ct);

        public Task<double> Average(Expression<Func<T, object>> selector, CancellationToken ct = default)
            => owner.ExecuteWithRetryAsync(store => buildQuery(store).Average(selector, ct), ct);
    }
}
