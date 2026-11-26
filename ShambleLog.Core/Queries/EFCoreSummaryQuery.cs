namespace ShambleLog.Core.Queries;

public class EFCoreSummaryQuery(ILoggingDbContext loggingDbContext)
{
    private readonly ILoggingDbContext _loggingDbContext = loggingDbContext;

    public class Result
    {
        public string[] TargetTables { get; set; } = [];
        public Dictionary<string, int> TagCounts { get; set; } = [];
        public int Count { get; set; }
        public long TotalElapsedMs { get; set; }
    }
    public async Task<Result[]> ExecuteAsync(string? timeExpression = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
