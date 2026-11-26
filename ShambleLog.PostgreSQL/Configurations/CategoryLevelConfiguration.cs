using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShambleLog.Core.Entities;

namespace ShambleLog.PostgreSQL.Configurations;

internal sealed class CategoryLevelConfiguration : IEntityTypeConfiguration<CategoryLevel>
{
    public void Configure(EntityTypeBuilder<CategoryLevel> builder)
    {
        builder.ToTable("CategoryLevels", "logging");
        builder.HasKey(level => level.Category);
    }
}
