using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public interface ILoggingDbContext
{
    DbSet<LogEntry> LogEntries { get; set; }
    DbSet<CategoryLevel> LogCategoryLevels { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
