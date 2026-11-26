using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core.Middleware;

/// <summary>
/// Implement on your logging context to use <see cref="EFCoreMiddleware"/>.
/// </summary>
public interface IEFCoreLoggingDbContext : ILoggingDbContext
{
    DbSet<EFCoreEntry> EFCoreEntries { get; set; }
}
