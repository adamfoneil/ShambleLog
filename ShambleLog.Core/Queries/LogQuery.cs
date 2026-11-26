using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;

namespace ShambleLog.Core.Queries;

public class LogQuery(ILoggingDbContext loggingDbContext)
{
    private readonly ILoggingDbContext _loggingDbContext = loggingDbContext;

    public class Criteria
    {
        public DateOnly Date { get; set; }
        /// <summary>
        /// Time expression e.g. "30m" (last 30 min) or "1:2h" (between 1 and 2 hours ago).
        /// Null means no time restriction within the selected Date.
        /// Can also use suffix ":asc" or "asc" by itself to sort results ascending -- used with TraceId/RequestId searching
        /// because the default sort is descending by timestamp, but you may want to see the earliest log entries first for a given TraceId/RequestId.
        /// </summary>
        public string? TimeExpression { get; set; }
        public string? Category { get; set; }
        public LogLevel? Level { get; set; }
        public string? TemplateContains { get; set; }
        public string? MessageContains { get; set; }
        /// <summary>
        /// Parses to a dictionary that affects where clause against the JSONB properties column, e.g. {"UserId": "123"}.
        /// Prefix a string property value with "-" to exclude matching logs, e.g. {"Method": "-GetLogEntriesAfterIdAsync"}.
        /// This is where you'd search for a specific TraceId or RequestId, e.g. {"TraceId": "abc123"}.
        /// </summary>
        public string? PropertiesContain { get; set; }
    }

    public class EFCoreCriteria
    {
        public string? Table { get; set; }
        public string? Tag { get; set; }
        public string? CommandTextContains { get; set; }
        public int? MinElapsedMs { get; set; }
    }

    /// <summary>
    /// queries main log table, attaching any EFCoreEntry data if present, but does not query EFCoreEntries table directly
    /// </summary>
    public async Task<LogEntry[]> ExecuteAsync(Criteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var properties = ParseProperties(criteria.PropertiesContain);
        IQueryable<LogEntry> query = _loggingDbContext.LogEntries.AsNoTracking();
        if (_loggingDbContext is IEFCoreLoggingDbContext)
            query = query.Include(entry => entry.EFCoreEntry);
        query = FilterProperties(query, properties);
        query = ApplyCriteria(query, criteria);
        var rows = await query.ToArrayAsync(cancellationToken);
#if SHAMBLELOG_POSTGRESQL
        return rows;
#else
        return properties.Length == 0 ? rows : rows.Where(entry => MatchesProperties(entry, properties)).ToArray();
#endif
    }

    public static IQueryable<LogEntry> ParseTimeExpression(IQueryable<LogEntry> query, string timeExpression)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(timeExpression);

        var expression = timeExpression.Trim();
        var ascending = expression.Equals("asc", StringComparison.OrdinalIgnoreCase) ||
            expression.EndsWith(":asc", StringComparison.OrdinalIgnoreCase);
        if (ascending)
        {
            expression = expression.Equals("asc", StringComparison.OrdinalIgnoreCase) ? "" : expression[..^4];
            if (expression.Length == 0 && !timeExpression.Trim().Equals("asc", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Invalid time expression.");
        }

        if (expression.Length > 0)
        {
            var match = Regex.Match(expression, @"^(\d+)(?::(\d+))?([smhd])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success ||
                !double.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
                (match.Groups[2].Success && !double.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new FormatException("Invalid time expression.");

            var second = match.Groups[2].Success
                ? double.Parse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture)
                : 0;
            if (match.Groups[2].Success && first > second)
                throw new FormatException("The start of a time range must not exceed its end.");

            var multiplier = char.ToLowerInvariant(match.Groups[3].Value[0]) switch
            {
                's' => 1d,
                'm' => 60d,
                'h' => 3600d,
                'd' => 86400d,
                _ => throw new FormatException("Invalid time expression.")
            };
            var now = DateTime.UtcNow;
            var earliestSeconds = (match.Groups[2].Success ? second : first) * multiplier;
            var latestSeconds = first * multiplier;
            if (!double.IsFinite(earliestSeconds) || earliestSeconds > (now - DateTime.MinValue).TotalSeconds)
                throw new FormatException("Time expression is out of range.");
            var earliest = now.AddSeconds(-earliestSeconds);
            query = query.Where(entry => entry.Timestamp >= earliest && entry.Timestamp <= now);
            if (match.Groups[2].Success)
            {
                var latest = now.AddSeconds(-latestSeconds);
                query = query.Where(entry => entry.Timestamp <= latest);
            }
        }

        return ascending ? query.OrderBy(entry => entry.Timestamp) : query.OrderByDescending(entry => entry.Timestamp);
    }
    
    /// <summary>
    /// queries EFCoreEntries table joined to main LogEntry table
    /// </summary>
    public async Task<EFCoreEntry[]> ExecuteAsync(EFCoreCriteria efCoreCriteria, Criteria? criteria = null, CancellationToken cancellationToken = default)
    {
        var efQueryDb = _loggingDbContext as IEFCoreLoggingDbContext ?? throw new NotSupportedException("EFCore query requires IEFCoreLoggingDbContext");
        ArgumentNullException.ThrowIfNull(efCoreCriteria);
        var properties = ParseProperties(criteria?.PropertiesContain);
        IQueryable<LogEntry> logs = FilterProperties(_loggingDbContext.LogEntries.AsNoTracking(), properties);
        if (criteria is not null)
            logs = ApplyCriteria(logs, criteria);

        var commands = efQueryDb.EFCoreEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(efCoreCriteria.Table))
            commands = commands.Where(entry => entry.TargetTables.Contains(efCoreCriteria.Table));
        if (!string.IsNullOrWhiteSpace(efCoreCriteria.Tag))
            commands = commands.Where(entry => entry.Tags.Contains(efCoreCriteria.Tag));
        if (!string.IsNullOrWhiteSpace(efCoreCriteria.CommandTextContains))
            commands = commands.Where(entry => entry.CommandText.Contains(efCoreCriteria.CommandTextContains));
        if (efCoreCriteria.MinElapsedMs is not null)
            commands = commands.Where(entry => entry.ElapsedMs >= efCoreCriteria.MinElapsedMs.Value);

        var joined = from command in commands
                     join log in logs on command.LogEntryId equals log.Id
                     select new { Command = command, Log = log };
        var ascending = IsAscending(criteria?.TimeExpression);
        joined = ascending ? joined.OrderBy(row => row.Log.Timestamp) : joined.OrderByDescending(row => row.Log.Timestamp);
        var rows = await joined.ToArrayAsync(cancellationToken);
#if !SHAMBLELOG_POSTGRESQL
        rows = rows.Where(row => MatchesProperties(row.Log, properties)).ToArray();
#endif
        return rows.Select(row =>
        {
            row.Command.LogEntry = row.Log;
            return row.Command;
        }).ToArray();
    }

    private static IQueryable<LogEntry> ApplyCriteria(IQueryable<LogEntry> query, Criteria criteria)
    {
        var start = criteria.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        query = query.Where(entry => entry.Timestamp >= start);
        if (criteria.Date != DateOnly.MaxValue)
        {
            var end = start.AddDays(1);
            query = query.Where(entry => entry.Timestamp < end);
        }
        if (!string.IsNullOrWhiteSpace(criteria.Category))
            query = query.Where(entry => entry.Category == criteria.Category);
        if (criteria.Level is not null)
            query = query.Where(entry => entry.Level == criteria.Level.Value);
        if (!string.IsNullOrWhiteSpace(criteria.TemplateContains))
            query = query.Where(entry => entry.Template.Contains(criteria.TemplateContains));
        if (!string.IsNullOrWhiteSpace(criteria.MessageContains))
            query = query.Where(entry => entry.Message.Contains(criteria.MessageContains));
        return ParseTimeExpression(query, criteria.TimeExpression ?? "");
    }

    private static bool IsAscending(string? expression) =>
        expression?.Trim().Equals("asc", StringComparison.OrdinalIgnoreCase) == true ||
        expression?.Trim().EndsWith(":asc", StringComparison.OrdinalIgnoreCase) == true;

    private static (string Key, JsonElement Value, bool Exclude)[] ParseProperties(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];
        using var document = JsonDocument.Parse(expression);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Properties must be a JSON object.");
        return document.RootElement.EnumerateObject().Select(property =>
        {
            var value = property.Value;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var exclude = text?.StartsWith('-') == true;
            return (property.Name, exclude ? JsonSerializer.SerializeToElement(text![1..]) : value.Clone(), exclude);
        }).ToArray();
    }

    private static IQueryable<LogEntry> FilterProperties(IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties)
    {
#if SHAMBLELOG_POSTGRESQL
        foreach (var property in properties)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [property.Key] = property.Value });
            query = property.Exclude
                ? query.Where(entry => !EF.Functions.JsonContains(entry.Properties, json))
                : query.Where(entry => EF.Functions.JsonContains(entry.Properties, json));
        }
#endif
        return query;
    }

    private static bool MatchesProperties(LogEntry entry, (string Key, JsonElement Value, bool Exclude)[] properties) =>
        properties.All(property =>
        {
            var matches = entry.Properties.TryGetValue(property.Key, out var value) &&
                JsonContains(JsonSerializer.SerializeToElement(value), property.Value);
            return property.Exclude ? !matches : matches;
        });

    private static bool JsonContains(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind == JsonValueKind.Object && expected.ValueKind == JsonValueKind.Object)
            return expected.EnumerateObject().All(property =>
                actual.TryGetProperty(property.Name, out var value) && JsonContains(value, property.Value));
        if (actual.ValueKind == JsonValueKind.Array && expected.ValueKind == JsonValueKind.Array)
            return expected.EnumerateArray().All(item =>
                actual.EnumerateArray().Any(value => JsonContains(value, item)));
        return JsonElement.DeepEquals(actual, expected);
    }
}
