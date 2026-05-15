namespace RunnerRunner.HostWorker.Services;

internal sealed class HostWorkerObservedLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly HostWorkerLogPublisher _publisher;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public HostWorkerObservedLoggerProvider(HostWorkerLogPublisher publisher)
    {
        _publisher = publisher;
    }

    public ILogger CreateLogger(string categoryName) => new HostWorkerObservedLogger(categoryName, _publisher, () => _scopeProvider);

    public void Dispose()
    {
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    private sealed class HostWorkerObservedLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly HostWorkerLogPublisher _publisher;
        private readonly Func<IExternalScopeProvider> _scopeProviderAccessor;

        public HostWorkerObservedLogger(
            string categoryName,
            HostWorkerLogPublisher publisher,
            Func<IExternalScopeProvider> scopeProviderAccessor)
        {
            _categoryName = categoryName;
            _publisher = publisher;
            _scopeProviderAccessor = scopeProviderAccessor;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _scopeProviderAccessor().Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (eventId.Id != 0)
                tags["event.id"] = eventId.Id.ToString();
            if (!string.IsNullOrWhiteSpace(eventId.Name))
                tags["event.name"] = eventId.Name!;

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    if (key != "{OriginalFormat}" && value != null)
                        tags[key] = value.ToString() ?? "";
                }
            }

            _scopeProviderAccessor().ForEachScope((scope, currentTags) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> scopeValues)
                {
                    foreach (var (key, value) in scopeValues)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && value != null)
                            currentTags[$"scope.{key}"] = value.ToString() ?? "";
                    }
                }
                else if (scope != null)
                {
                    currentTags.TryAdd("scope", scope.ToString() ?? "");
                }
            }, tags);

            _publisher.PublishProcessLog(_categoryName, logLevel, message, exception, tags);
        }
    }
}
