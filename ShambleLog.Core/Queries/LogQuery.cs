using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;

namespace ShambleLog.Core.Queries;

public abstract class LogQuery(ILoggingDbContext loggingDbContext)
{
    private readonly ILoggingDbContext _loggingDbContext = loggingDbContext;

    public class Criteria
    {
        public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
        /// <summary>
        /// Time expression e.g. "30m" (last 30 min) or "1:2h" (between 1 and 2 hours ago).
        /// nnnn:nnnn defines an exact range (e.g. 0930:1330 for 9:30am to 1:30pm), and nnnn alone defines a range from nnnn to now (e.g. 0930 for 9:30am to now).
        /// Null means no time restriction within the selected Date.
        /// Can also use suffix ":asc" or "asc" by itself to sort results ascending -- used with TraceId/RequestId searching
        /// because the default sort is descending by timestamp, but you may want to see the earliest log entries first for a given TraceId/RequestId.
        /// </summary>
        public string? TimeExpression { get; set; }
        public string? CategoryContains { get; set; }
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
    public async Task<LogEntry[]> ExecuteAsync(Criteria criteria, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var properties = ParseProperties(criteria.PropertiesContain);
        IQueryable<LogEntry> query = _loggingDbContext.LogEntries.AsNoTracking();
        if (_loggingDbContext is IEFCoreLoggingDbContext)
            query = query.Include(entry => entry.EFCoreEntry);
        var (filteredQuery, clientFilter) = FilterProperties(query, properties);
        filteredQuery = ApplyCriteria(filteredQuery, criteria);
        var rows = await filteredQuery.Skip(skip).Take(take).TagWith("ShambleLog Internal").ToArrayAsync(cancellationToken);
        return clientFilter is null ? rows : [.. rows.Where(clientFilter)];
    }

    public static IQueryable<LogEntry> ParseTimeExpression(IQueryable<LogEntry> query, string timeExpression) =>
        ParseTimeExpression(query, timeExpression, null);

    private static IQueryable<LogEntry> ParseTimeExpression(IQueryable<LogEntry> query, string timeExpression, DateOnly? date)
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
            var clockMatch = Regex.Match(expression, @"^([0-9]{4})(?::([0-9]{4}))?$", RegexOptions.CultureInvariant);
            if (clockMatch.Success)
            {
                var startTime = ParseClockTime(clockMatch.Groups[1].Value);
                var now = DateTime.UtcNow;
                var selectedDate = date ?? DateOnly.FromDateTime(now);
                var start = selectedDate.ToDateTime(startTime, DateTimeKind.Utc);
                var end = clockMatch.Groups[2].Success
                    ? selectedDate.ToDateTime(ParseClockTime(clockMatch.Groups[2].Value), DateTimeKind.Utc)
                    : now;
                if (clockMatch.Groups[2].Success && start > end)
                    throw new FormatException("The start of a time range must not exceed its end.");
                query = query.Where(entry => entry.Timestamp >= start && entry.Timestamp <= end);
            }
            else
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
        }

        return ascending ? query.OrderBy(entry => entry.Timestamp) : query.OrderByDescending(entry => entry.Timestamp);
    }

    private static TimeOnly ParseClockTime(string value)
    {
        if (!TimeOnly.TryParseExact(value, "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            time.ToString("HHmm", CultureInfo.InvariantCulture) != value)
            throw new FormatException("Invalid time expression.");
        return time;
    }

    /// <summary>
    /// queries EFCoreEntries table joined to main LogEntry table
    /// </summary>
    public async Task<EFCoreEntry[]> ExecuteAsync(EFCoreCriteria efCoreCriteria, int skip, int take, Criteria? criteria = null, CancellationToken cancellationToken = default)
    {
        var efQueryDb = _loggingDbContext as IEFCoreLoggingDbContext ?? throw new NotSupportedException("EFCore query requires IEFCoreLoggingDbContext");
        ArgumentNullException.ThrowIfNull(efCoreCriteria);
        var properties = ParseProperties(criteria?.PropertiesContain);
        var (logs, clientFilter) = FilterProperties(_loggingDbContext.LogEntries.AsNoTracking(), properties);
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
        var rows = await joined.Skip(skip).Take(take).TagWith("ShambleLog Internal").ToArrayAsync(cancellationToken);
        if (clientFilter is not null)
            rows = [.. rows.Where(row => clientFilter(row.Log))];
        return [.. rows.Select(row =>
        {
            row.Command.LogEntry = row.Log;
            return row.Command;
        })];
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
        if (!string.IsNullOrWhiteSpace(criteria.CategoryContains))
            query = query.Where(entry => entry.Category.ToLower().Contains(criteria.CategoryContains.ToLower()));
        if (criteria.Level is not null)
            query = query.Where(entry => entry.Level == criteria.Level.Value);
        if (!string.IsNullOrWhiteSpace(criteria.TemplateContains))
            query = query.Where(entry => entry.Template.Contains(criteria.TemplateContains));
        if (!string.IsNullOrWhiteSpace(criteria.MessageContains))
            query = query.Where(entry => entry.Message.Contains(criteria.MessageContains));
        return ParseTimeExpression(query, criteria.TimeExpression ?? "", criteria.Date);
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
        return [.. document.RootElement.EnumerateObject().Select(property =>
        {
            var value = property.Value;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var exclude = text?.StartsWith('-') == true;
            return (property.Name, exclude ? JsonSerializer.SerializeToElement(text![1..]) : value.Clone(), exclude);
        })];
    }

    /// <summary>
    /// Applies provider-side property filters and optionally returns a predicate for filtering materialized entries.
    /// Properties may be empty; excluded values have already had their leading minus sign removed.
    /// Return a null predicate when all filtering is performed by the query.
    /// </summary>
    protected abstract (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) FilterProperties(
        IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties);
}

public class ClientSideLogQuery(ILoggingDbContext loggingDbContext) : LogQuery(loggingDbContext)
{
    protected override (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) FilterProperties(
        IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties) =>
        (query, properties.Length == 0 ? null : entry => MatchesProperties(entry, properties));

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
