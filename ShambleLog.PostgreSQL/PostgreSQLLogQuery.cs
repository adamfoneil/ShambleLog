using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShambleLog.Core;
using ShambleLog.Core.Entities;
using ShambleLog.Core.Queries;

namespace ShambleLog.PostgreSQL;

public class PostgreSQLLogQuery(ILoggingDbContext loggingDbContext) : LogQuery(loggingDbContext)
{
    protected override (IQueryable<LogEntry> Query, Func<LogEntry, bool>? ClientFilter) FilterProperties(
        IQueryable<LogEntry> query, (string Key, JsonElement Value, bool Exclude)[] properties)
    {
        foreach (var (Key, Value, Exclude) in properties)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [Key] = Value });
            query = Exclude
                ? query.Where(entry => !EF.Functions.JsonContains(entry.Properties, json))
                : query.Where(entry => EF.Functions.JsonContains(entry.Properties, json));
        }
        return (query, null);
    }
}
