using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Middleware;

namespace ShambleLog.Core.Queries;

public enum EFCoreMetricsQueryType
{
    Tag,
    Table    
}

public class EFCoreMetricsQuery(ILoggingDbContext loggingDbContext)
{
    private readonly IEFCoreLoggingDbContext _loggingDbContext = loggingDbContext as IEFCoreLoggingDbContext ?? throw new NotSupportedException("EFCore query requires IEFCoreLoggingDbContext");

    public class Result
    {
        public EFCoreMetricsQueryType Type { get; set; }
        /// <summary>
        /// tag or table name
        /// </summary>
        public string Name { get; set; } = default!;
        public long MinElapsedMs { get; set; }
        public long MaxElapsedMs { get; set; }
        public double AvgElapsedMs { get; set; }
        /// <summary>The 95th percentile using the nearest-rank method.</summary>
        public double P95ElapsedMs { get; set; }
        public int Count { get; set; }
    }

    public async Task<Result[]> ExecuteAsync(DateOnly? date, string? timeExpression, CancellationToken cancellationToken)
    {
        var logs = LogQuery.ApplyDateFilter(_loggingDbContext.LogEntries.AsNoTracking(), date);
        if (timeExpression is not null)
            logs = LogQuery.ParseTimeExpression(logs, timeExpression, date);

        var commands = from command in _loggingDbContext.EFCoreEntries.AsNoTracking()
                       join log in logs on command.LogEntryId equals log.Id
                       select new { command.Tags, command.TargetTables, command.ElapsedMs };
        var rows = await commands.TagAsShambleLogInternal().ToArrayAsync(cancellationToken);

        var metrics = rows.SelectMany(row =>
            NormalizeValues(row.Tags)
                .Select(name => (Type: EFCoreMetricsQueryType.Tag, Name: name, row.ElapsedMs))
                .Concat(NormalizeValues(row.TargetTables)
                    .Select(name => (Type: EFCoreMetricsQueryType.Table, Name: name, row.ElapsedMs))));

        return [.. metrics
            .GroupBy(row => (row.Type, Name: row.Name.ToUpperInvariant()))
            .Select(group =>
            {
                var elapsed = group.Select(row => row.ElapsedMs).OrderBy(value => value).ToArray();
                return new Result
                {
                    Type = group.Key.Type,
                    Name = group.First().Name,
                    MinElapsedMs = elapsed[0],
                    MaxElapsedMs = elapsed[^1],
                    AvgElapsedMs = elapsed.Average(value => (double)value),
                    P95ElapsedMs = elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1],
                    Count = elapsed.Length
                };
            })
            .OrderByDescending(result => result.Count)
            .ThenByDescending(result => result.AvgElapsedMs)
            .ThenBy(result => result.Type)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] NormalizeValues(string[]? values) =>
        [.. (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];
}
