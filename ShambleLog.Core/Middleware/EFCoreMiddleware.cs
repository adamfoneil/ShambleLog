using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ShambleLog.Core.Middleware;

public class EFCoreMiddleware(ILoggingDbContext db) : ILogEntryMiddleware
{
    public static readonly string Category = DbLoggerCategory.Database.Command.Name;

    // RelationalEventId.CommandExecuted / CommandError (Microsoft.EntityFrameworkCore.Relational)
    private const int CommandExecutedEventId = 20101;
    private const int CommandErrorEventId = 20102;

    public Task InvokeAsync(LogEntryContext context, LogEntryDelegate next)
    {
        if (!IsCommandResult(context.Entry))
            return next(context);

        if (db is not IEFCoreLoggingDbContext efDb)
            throw new InvalidOperationException(
                $"{nameof(EFCoreMiddleware)} requires the logging context to implement {nameof(IEFCoreLoggingDbContext)}.");

        var (tables, tags, commandText, parameters, elapsed) = ParseLogEntry(context.Entry);

        // Id is assigned when the pipeline saves; the navigation lets EF Core set the foreign key.
        efDb.EFCoreEntries.Add(new EFCoreEntry
        {
            LogEntry = context.Entry,
            TargetTables = tables,
            Tags = tags,
            CommandText = commandText,
            ElapsedMs = elapsed,
            Parameters = parameters
        });

        return next(context);
    }

    private static bool IsCommandResult(LogEntry entry) =>
        entry.Properties.TryGetValue("EventId", out var eventId)
        && eventId is CommandExecutedEventId or CommandErrorEventId;

    private static (string[] tableNames, string[] tags, string commandText, string? parameters, long elapsed) ParseLogEntry(LogEntry logViewEntry)
    {
        var message = logViewEntry.Message ?? string.Empty;
        var properties = logViewEntry.Properties;

        var elapsed = 0L;
        if (properties.TryGetValue("elapsed", out var elapsedValue)
            && decimal.TryParse(Convert.ToString(elapsedValue, CultureInfo.InvariantCulture),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var elapsedMs))
            elapsed = (long)elapsedMs;
        else
        {
            var elapsedMatch = Regex.Match(message, @"\(([\d,]+)ms\)", RegexOptions.IgnoreCase);
            if (elapsedMatch.Success && long.TryParse(elapsedMatch.Groups[1].Value, NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var parsedElapsed))
                elapsed = parsedElapsed;
        }

        string? parameters;
        if (properties.TryGetValue("parameters", out var parametersValue))
            parameters = parametersValue as string;
        else
        {
            var parametersMatch = Regex.Match(message, @"Parameters=\[(.*?)\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            parameters = parametersMatch.Success ? parametersMatch.Groups[1].Value : null;
        }
        if (string.IsNullOrWhiteSpace(parameters))
            parameters = null;
        else
            parameters = parameters.Trim();

        string commandText;
        if (properties.TryGetValue("commandText", out var commandTextValue) && commandTextValue is string text)
            commandText = text.Trim();
        else
        {
            var commandTextMatch = Regex.Match(message, @"\]\s*(?:\r?\n)+(?<sql>[\s\S]+)$", RegexOptions.Multiline);
            commandText = commandTextMatch.Success
                ? commandTextMatch.Groups["sql"].Value.Trim()
                : message.Trim();
        }

        var tags = commandText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("--"))
            .Select(line => line[2..].Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tableNames = ParseTableNames(commandText);

        return (tableNames, tags, commandText, parameters, elapsed);
    }

    private static string[] ParseTableNames(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText)) return [];

        var tableMatches = Regex.Matches(
            commandText,
            @"\b(?:FROM|JOIN|UPDATE|INTO|DELETE\s+FROM)\s+([^\s,;()]+)",
            RegexOptions.IgnoreCase);

        var tableNames = tableMatches
            .Select(match => match.Groups[1].Value
                .Trim()
                .Trim('"', '\'', '`', '[', ']')
                .Replace("\"", "")
                .Replace("'", "")
                .Replace("`", "")
                .Replace("[", "")
                .Replace("]", ""))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tableNames;
    }
}
