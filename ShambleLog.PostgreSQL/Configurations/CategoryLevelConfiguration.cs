using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShambleLog.Core.Entities;

namespace ShambleLog.PostgreSQL.Configurations;

public sealed class CategoryLevelConfiguration : IEntityTypeConfiguration<CategoryLevel>
{
    public void Configure(EntityTypeBuilder<CategoryLevel> builder)
    {
        builder.ToTable("CategoryLevels", "logging");
        builder.HasKey(level => level.Category);
    }
}
