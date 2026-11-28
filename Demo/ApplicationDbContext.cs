using Microsoft.EntityFrameworkCore;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Middleware;
using ShambleLog.PostgreSQL;

namespace Demo;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IEFCoreLoggingDbContext
{
    public DbSet<EFCoreEntry> EFCoreEntries { get; set; }
    public DbSet<LogEntry> LogEntries { get; set; }
    public DbSet<CategoryLevel> LogCategoryLevels { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureShambleLog();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
