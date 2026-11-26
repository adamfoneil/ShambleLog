using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;

namespace ShambleLog.Core.Queries;

public class LogQuery(ILoggingDbContext loggingDbContext)
{
    private readonly ILoggingDbContext _loggingDbContext = loggingDbContext;

    public class Criteria
    {
        /// <summary>
        /// Time expression e.g. "30m" (last 30 min) or "1:2h" (between 1 and 2 hours ago).
        /// Null means no time restriction within the selected date.
        /// Can also use suffix ":asc" or "asc" by itself to sort results ascending -- used with TraceId/RequestId searching
        /// </summary>
        public string? TimeExpression { get; set; }
        public string? Category { get; set; }
        public LogLevel? Level { get; set; }
        public string? TemplateContains { get; set; }
        public string? MessageContains { get; set; }
        /// <summary>
        /// Parses to a dictionary that affects where clause against the JSONB properties column, e.g. {"UserId": "123"}.
        /// Prefix a property value with "-" to exclude matching logs, e.g. "Method:-GetLogEntriesAfterIdAsync".
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
        throw new NotImplementedException();
    }

    public static IQueryable<LogEntry> ParseTimeExpression(IQueryable<LogEntry> query, string timeExpression)
    {
        throw new NotImplementedException();
    }
    
    /// <summary>
    /// queries EFCoreEntries table joined to main LogEntry table
    /// </summary>
    public async Task<EFCoreEntry[]> ExecuteAsync(EFCoreCriteria efCoreCriteria, Criteria? criteria = null, CancellationToken cancellationToken = default)
    {
        var efQueryDb = _loggingDbContext as IEFCoreLoggingDbContext ?? throw new NotSupportedException("EFCore query requires IEFCoreLoggingDbContext");
        throw new NotImplementedException();
    }
}
