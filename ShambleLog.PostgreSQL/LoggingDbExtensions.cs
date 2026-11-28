using Microsoft.EntityFrameworkCore;
using ShambleLog.PostgreSQL.Configurations;

namespace ShambleLog.PostgreSQL;

public static class LoggingDbContextExtensions
{
    public static void ConfigureShambleLog(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LogEntryConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryLevelConfiguration());
        modelBuilder.ApplyConfiguration(new EFCoreEntryConfiguration());
    }
}