using Microsoft.EntityFrameworkCore;

namespace ShambleLog.Core;

public interface ILoggingDbContext
{
    DbSet<LogEntry> LogEntries { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
