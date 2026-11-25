using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public interface ILoggingDbContext : ILoggingDbContext<LogEntry>
{
}

public interface ILoggingDbContext<TLogEntry> where TLogEntry : LogEntry
{
    DbSet<TLogEntry> LogEntries { get; set; }
    DbSet<CategoryLevel> LogCategoryLevels { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
