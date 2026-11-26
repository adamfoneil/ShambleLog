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

    public async Task<Result[]> ExecuteAsync(string? timeExpression = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
