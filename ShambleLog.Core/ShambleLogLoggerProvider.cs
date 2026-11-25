using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using System.Globalization;

namespace ShambleLog.Core;

public sealed class ShambleLogLoggerProvider(LogEntryQueue queue) : ILoggerProvider, ISupportExternalScope
{
    private readonly LogEntryQueue _queue = queue;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new ShambleLogLogger(categoryName, _queue, () => _scopeProvider);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose() { }

    private sealed class ShambleLogLogger(string categoryName, LogEntryQueue queue, Func<IExternalScopeProvider> scopeProvider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopeProvider().Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && !queue.SuppressLogging;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            ArgumentNullException.ThrowIfNull(formatter);

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = logLevel,
                Category = categoryName,
                Message = formatter(state, exception),
                Exception = exception?.ToString()
            };

            var hasTemplate = false;
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var property in properties)
                {
                    if (property.Key == "{OriginalFormat}")
                    {
                        entry.Template = Convert.ToString(property.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                        hasTemplate = true;
                    }
                    else
                        entry.Properties[property.Key] = SnapshotValue(property.Value);
                }
            }

            if (!hasTemplate)
                entry.Template = entry.Message;

            if (eventId.Id != 0 || eventId.Name is not null)
            {
                entry.Properties["EventId"] = eventId.Id;
                if (eventId.Name is not null)
                    entry.Properties["EventName"] = eventId.Name;
            }

            var scopes = new List<string>();
            scopeProvider().ForEachScope((scope, list) => list.Add(Convert.ToString(scope, CultureInfo.InvariantCulture) ?? string.Empty), scopes);
            if (scopes.Count > 0)
                entry.Properties["Scopes"] = scopes;

            queue.Enqueue(entry);
        }

        private static object? SnapshotValue(object? value) => value switch
        {
            null => null,
            float number when !float.IsFinite(number) => number.ToString(CultureInfo.InvariantCulture),
            double number when !double.IsFinite(number) => number.ToString(CultureInfo.InvariantCulture),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
}
