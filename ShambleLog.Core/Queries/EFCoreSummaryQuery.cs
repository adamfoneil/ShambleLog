using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;

namespace ShambleLog.Core.Queries;

public class EFCoreSummaryQuery(ILoggingDbContext loggingDbContext)
{
    private readonly IEFCoreLoggingDbContext _loggingDbContext = loggingDbContext as IEFCoreLoggingDbContext ?? throw new NotSupportedException("EFCore query requires IEFCoreLoggingDbContext");

    public class Result
    {
        public string[] FromTables { get; set; } = [];
        public string[] QueryTags { get; set; } = [];
        public int TotalCalls { get; set; }
        public long MinElapsedMs { get; set; }
        public long MaxElapsedMs { get; set; }
        public double AvgElapsedMs { get; set; }
        /// <summary>
        /// The most recent timestamp among the raw log entries that make up this metric group.
        /// </summary>
        public DateTime LatestTimestamp { get; set; }
    }
    public async Task<Result[]> ExecuteAsync(string? timeExpression = null, CancellationToken cancellationToken = default)
    {
        var logs = _loggingDbContext.LogEntries.AsNoTracking();
        if (timeExpression is not null)
            logs = LogQuery.ParseTimeExpression(logs, timeExpression);

        var joined = from command in _loggingDbContext.EFCoreEntries.AsNoTracking()
                     join log in logs on command.LogEntryId equals log.Id
                     select new { Command = command, Log = log };
        var rows = await joined.ToArrayAsync(cancellationToken);
        return BuildMetrics(rows.Select(row =>
        {
            row.Command.LogEntry = row.Log;
            return row.Command;
        })).ToArray();
    }

    internal static List<Result> BuildMetrics(IEnumerable<EFCoreEntry> rows) =>
        [.. rows
            .Select(r => new
            {
                FromTables = NormalizeValues(r.TargetTables),
                QueryTags = NormalizeValues(r.Tags),
                r.ElapsedMs,
                r.LogEntry.Timestamp
            })
            .GroupBy(r => new
            {
                FromTables = ConcatenateForGrouping(r.FromTables).ToUpperInvariant(),
                QueryTags = ConcatenateForGrouping(r.QueryTags).ToUpperInvariant()
            })
            .Select(g => new Result
            {
                FromTables = g.First().FromTables,
                QueryTags = g.First().QueryTags,
                TotalCalls = g.Count(),
                MinElapsedMs = g.Min(x => x.ElapsedMs),
                MaxElapsedMs = g.Max(x => x.ElapsedMs),
                AvgElapsedMs = g.Average(x => (double)x.ElapsedMs),
                LatestTimestamp = g.Max(x => x.Timestamp)
            })
            .OrderByDescending(m => m.TotalCalls)
            .ThenByDescending(m => m.AvgElapsedMs)];

    internal static string[] NormalizeValues(string[]? values) =>
        [.. (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)];

    internal static string ConcatenateForGrouping(string[]? values)
    {
        var result = NormalizeValues(values);
        return result.Length == 0 ? "(none)" : string.Join(", ", result);
    }
}
