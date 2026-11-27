using Microsoft.Extensions.Logging;

namespace ShambleLog.RCL;

internal static class CssUtil
{
    internal static string? GetClassName(this Dictionary<string, object> properties) => properties.GetValueOrDefault("class")?.ToString();

    internal static string GetLogLevelBadgeClass(LogLevel level) => level switch
    {
        LogLevel.Trace => "bg-secondary",
        LogLevel.Debug => "bg-info text-dark",
        LogLevel.Information => "bg-primary",
        LogLevel.Warning => "bg-warning text-dark",
        LogLevel.Error => "bg-danger",
        LogLevel.Critical => "bg-dark",
        _ => "bg-secondary"
    };

    internal static string GetLogLevelBadgeClass(string? level) =>
        Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsedLevel)
            ? GetLogLevelBadgeClass(parsedLevel)
            : "bg-secondary";    
}
