using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShambleLog.Core.Entities;

namespace ShambleLog.PostgreSQL.Configurations;

public sealed class LogEntryConfiguration : IEntityTypeConfiguration<LogEntry>
{
    public void Configure(EntityTypeBuilder<LogEntry> builder)
    {
        builder.ToTable("Entries", "logging");
        builder.HasKey(entry => entry.Id);

        var comparer = new ValueComparer<Dictionary<string, object?>>(
            (left, right) => SerializeProperties(left) == SerializeProperties(right),
            properties => SerializeProperties(properties).GetHashCode(),
            properties => DeserializeProperties(SerializeProperties(properties)));

        builder.Property(entry => entry.Properties)
            .HasConversion(
                properties => SerializeProperties(properties),
                json => DeserializeProperties(json))
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(comparer);
    }

    private static string SerializeProperties(Dictionary<string, object?>? properties) =>
        JsonSerializer.Serialize(properties);

    private static Dictionary<string, object?> DeserializeProperties(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
        ?? throw new InvalidOperationException("Invalid log properties JSON.");
}
