using Microsoft.Extensions.Logging;

namespace ShambleLog.Core.Entities;

public class LogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public Dictionary<string, object?> Properties { get; set; } = [];

    /// <summary>
    /// override this to set additional properties you may have customized on your LogEntry entity, 
    /// for example extracting select values from the properties blob to save in defined columns
    /// </summary>
    public virtual void SetProperties(string category, LogLevel level, string template, string message, string? exception, Dictionary<string, object?> properties)
    {
        Category = category;
        Level = level;
        Template = template;
        Message = message;
        Exception = exception;
        Properties = properties;
    }
}
