using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SmartMetrix.ServiceDefaults;

internal sealed class JsonFileLoggerProvider(string rootDirectory, string serviceName) :
    ILoggerProvider,
    ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, JsonFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new JsonFileLogger(this, category));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() => _loggers.Clear();

    internal void Write<TState>(string category, LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            var serviceDirectory = Path.Combine(rootDirectory, Sanitize(serviceName));
            Directory.CreateDirectory(serviceDirectory);
            var path = Path.Combine(serviceDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    if (value.Key != "{OriginalFormat}") properties[value.Key] = value.Value;
                }
            }
            _scopes.ForEachScope((scope, target) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> scopedValues)
                {
                    foreach (var value in scopedValues) target[value.Key] = value.Value;
                }
                else if (scope is not null) target["Scope"] = scope.ToString();
            }, properties);

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = level.ToString(),
                service = serviceName,
                category,
                eventId = eventId.Id,
                message = formatter(state, exception),
                properties,
                exception = exception?.ToString()
            };
            var line = JsonSerializer.Serialize(entry);
            lock (_writeLock) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // File logging must never terminate a measurement service.
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private sealed class JsonFileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, state, exception, formatter);
        }
    }
}
