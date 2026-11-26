using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShambleLog.Core.Entities;

namespace ShambleLog.PostgreSQL.Configurations;

public sealed class EFCoreEntryConfiguration : IEntityTypeConfiguration<EFCoreEntry>
{
    public void Configure(EntityTypeBuilder<EFCoreEntry> builder)
    {
        builder.ToTable("EFCoreEntries", "logging");
        builder.HasKey(entry => entry.LogEntryId);
        builder.HasOne(entry => entry.LogEntry)
            .WithOne()
            .HasForeignKey<EFCoreEntry>(entry => entry.LogEntryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
