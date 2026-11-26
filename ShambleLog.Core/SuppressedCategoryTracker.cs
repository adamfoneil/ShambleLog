using Microsoft.Extensions.Logging;

namespace ShambleLog.Core;

public sealed class SuppressedCategoryTracker
{
    private readonly object _lock = new();
    private readonly HashSet<(string Category, LogLevel SuppressedLevel)> _entries = [];

    public (string Category, LogLevel SuppressedLevel)[] Snapshot
    {
        get
        {
            lock (_lock)
                return _entries.ToArray();
        }
    }

    internal void Record(string category, LogLevel suppressedLevel)
    {
        lock (_lock)
            _entries.Add((category, suppressedLevel));
    }
}
