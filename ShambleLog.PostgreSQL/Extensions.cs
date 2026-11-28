using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShambleLog.Core;
using ShambleLog.PostgreSQL.Configurations;

namespace ShambleLog.PostgreSQL;

public static class Extensions
{
    public static void AddShambleLogPostgreSQL<TDbContext>(this ILoggingBuilder loggingBuilder, Action<ShambleLogBuilder>? configure)
        where TDbContext : DbContext, ILoggingDbContext => 
        Registration.Add<TDbContext, PostgreSQLLogQuery>(loggingBuilder, configure);

    public static void ConfigureShambleLog(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LogEntryConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryLevelConfiguration());
        modelBuilder.ApplyConfiguration(new EFCoreEntryConfiguration());
    }
}