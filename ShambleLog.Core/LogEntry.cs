using Microsoft.Extensions.Logging;

namespace ShambleLog.Core;

public class LogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; }
    public string SourceContext { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public Dictionary<string, object?> Properties { get; set; } = [];
}
