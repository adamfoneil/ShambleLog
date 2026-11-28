using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ShambleLog.Core.Queries;

public class CategorySummaryQuery(ILoggingDbContext loggingDbContext)
{
    private readonly ILoggingDbContext _loggingDbContext = loggingDbContext;

    public class Result
    {
        public string Category { get; set; } = default!;
        public Dictionary<LogLevel, int> LevelCounts { get; set; } = [];
    }

    public async Task<Result[]> ExecuteAsync(DateOnly? date = null, string? timeExpression = null, CancellationToken cancellationToken = default)
    {
        var query = LogQuery.ApplyDateFilter(_loggingDbContext.LogEntries.AsNoTracking(), date);
        if (timeExpression is not null)
            query = LogQuery.ParseTimeExpression(query, timeExpression, date);

        var counts = await query
            .GroupBy(entry => new { entry.Category, entry.Level })
            .Select(group => new { group.Key.Category, group.Key.Level, Count = group.Count() })
            .TagWith("ShambleLog Internal")
            .ToArrayAsync(cancellationToken);

        return [.. counts
            .GroupBy(count => count.Category)
            .Select(group => new Result
            {
                Category = group.Key,
                LevelCounts = group.ToDictionary(count => count.Level, count => count.Count)
            })];
    }
}
