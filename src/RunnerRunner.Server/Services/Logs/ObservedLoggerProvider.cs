using System.Diagnostics;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Server.Services.Logs;

public sealed class ObservedLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ObservedLogStore _store;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ObservedLoggerProvider(ObservedLogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName) => new ObservedLogger(categoryName, _store, () => _scopeProvider);

    public void Dispose()
    {
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    private sealed class ObservedLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ObservedLogStore _store;
        private readonly Func<IExternalScopeProvider> _scopeProviderAccessor;

        public ObservedLogger(
            string categoryName,
            ObservedLogStore store,
            Func<IExternalScopeProvider> scopeProviderAccessor)
        {
            _categoryName = categoryName;
            _store = store;
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
            if (string.IsNullOrEmpty(message) && exception == null)
                return;

            var activity = Activity.Current;
            var entry = new ObservedLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                SourceType = ClassifySource(_categoryName),
                SourceId = ClassifySource(_categoryName) == ObservedLogSourceType.Grain ? "server:grains" : "server",
                SourceName = ClassifySource(_categoryName) == ObservedLogSourceType.Grain ? "Orleans grains" : "Server",
                Category = _categoryName,
                StreamKind = ClassifyStream(_categoryName),
                Level = ToObservedLevel(logLevel),
                Message = message,
                RenderedMessage = FormatRenderedLine(logLevel, _categoryName, eventId, message, exception),
                Exception = exception?.ToString(),
                TraceId = activity?.TraceId.ToString(),
                SpanId = activity?.SpanId.ToString()
            };

            foreach (var (key, value) in ReadTags(state, eventId))
                entry.Tags[key] = value;
            AddScopes(entry);

            _store.Add(entry);
        }

        private void AddScopes(ObservedLogEntry entry)
        {
            _scopeProviderAccessor().ForEachScope((scope, currentEntry) =>
            {
                switch (scope)
                {
                    case IEnumerable<KeyValuePair<string, object?>> values:
                        foreach (var (key, value) in values)
                        {
                            if (!string.IsNullOrWhiteSpace(key) && value != null)
                                currentEntry.Tags[$"scope.{key}"] = value.ToString() ?? "";
                        }
                        break;
                    case not null:
                        currentEntry.Tags.TryAdd("scope", scope.ToString() ?? "");
                        break;
                }
            }, entry);
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadTags<TState>(TState state, EventId eventId)
        {
            if (eventId.Id != 0)
                yield return new("event.id", eventId.Id.ToString());
            if (!string.IsNullOrWhiteSpace(eventId.Name))
                yield return new("event.name", eventId.Name!);

            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
                yield break;

            foreach (var (key, value) in values)
            {
                if (key == "{OriginalFormat}" || value == null)
                    continue;
                yield return new(key, value.ToString() ?? "");
            }
        }

        private static ObservedLogSourceType ClassifySource(string category)
            => category.StartsWith("RunnerRunner.Server.Grains.", StringComparison.Ordinal)
               || category.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal)
                ? ObservedLogSourceType.Grain
                : ObservedLogSourceType.Server;

        private static ObservedLogStreamKind ClassifyStream(string category)
            => category.StartsWith("RunnerRunner.Server.Grains.", StringComparison.Ordinal)
               || category.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal)
                ? ObservedLogStreamKind.Grain
                : ObservedLogStreamKind.Application;

        private static ObservedLogLevel ToObservedLevel(LogLevel logLevel) => logLevel switch
        {
            LogLevel.Trace => ObservedLogLevel.Trace,
            LogLevel.Debug => ObservedLogLevel.Debug,
            LogLevel.Information => ObservedLogLevel.Information,
            LogLevel.Warning => ObservedLogLevel.Warning,
            LogLevel.Error => ObservedLogLevel.Error,
            LogLevel.Critical => ObservedLogLevel.Critical,
            _ => ObservedLogLevel.None
        };

        private static string FormatRenderedLine(
            LogLevel level,
            string category,
            EventId eventId,
            string message,
            Exception? exception)
        {
            var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");
            var suffix = eventId.Id == 0 && string.IsNullOrWhiteSpace(eventId.Name)
                ? ""
                : $" [{eventId.Id}{(string.IsNullOrWhiteSpace(eventId.Name) ? "" : ":" + eventId.Name)}]";
            var line = $"{timestamp} {LevelToken(level)} {ShortCategory(category)}{suffix}: {message}";
            return exception == null ? line : line + Environment.NewLine + exception;
        }

        private static string LevelToken(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };

        private static string ShortCategory(string category)
        {
            const string serverPrefix = "RunnerRunner.Server.";
            return category.StartsWith(serverPrefix, StringComparison.Ordinal)
                ? category[serverPrefix.Length..]
                : category;
        }
    }
}
