using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core.Queries;

public class CategoryLevelQuery(IServiceScopeFactory scopeFactory)
{
    public async Task<CategoryLevel[]> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ILoggingDbContext>();
        return await context.LogCategoryLevels.AsNoTracking().OrderBy(item => item.Category)
            .TagAsShambleLogInternal().ToArrayAsync(cancellationToken);
    }

    public async Task SaveAsync(CategoryLevel value, string? originalCategory = null, CancellationToken cancellationToken = default)
    {
        var category = value.Category?.Trim();
        if (string.IsNullOrEmpty(category) || !Enum.IsDefined(value.Level))
            throw new InvalidOperationException("Enter a category and a valid log level.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ILoggingDbContext>();
        var rules = await context.LogCategoryLevels.TagAsShambleLogInternal().ToListAsync(cancellationToken);
        var original = originalCategory is null ? null : rules.SingleOrDefault(item => item.Category == originalCategory)
            ?? throw new InvalidOperationException("The category level no longer exists. Refresh and try again.");
        if (original is not null && string.Equals(original.Category, "Default", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(original.Category, category, StringComparison.Ordinal))
            throw new InvalidOperationException("The Default category cannot be renamed.");
        if (rules.Any(item => item != original && string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A category level with this name already exists.");

        if (original is not null && original.Category == category)
        {
            original.Level = value.Level;
            original.IsTemporary = value.IsTemporary;
        }
        else
        {
            if (original is not null)
                context.LogCategoryLevels.Remove(original);
            context.LogCategoryLevels.Add(new CategoryLevel { Category = category, Level = value.Level, IsTemporary = value.IsTemporary });
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCategoryLevelAsync(string category, LogLevel level, CancellationToken cancellationToken = default)
    {
        var existing = (await ExecuteAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(new CategoryLevel
        {
            Category = existing?.Category ?? category,
            Level = level,
            IsTemporary = existing?.IsTemporary ?? true
        }, existing?.Category, cancellationToken);
    }

    public async Task DeleteAsync(string category, CancellationToken cancellationToken = default)
    {
        if (string.Equals(category, "Default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Default category cannot be deleted.");
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ILoggingDbContext>();
        var rule = await context.LogCategoryLevels.TagAsShambleLogInternal()
            .SingleOrDefaultAsync(item => item.Category == category, cancellationToken)
            ?? throw new InvalidOperationException("The category level no longer exists. Refresh and try again.");
        context.LogCategoryLevels.Remove(rule);
        await context.SaveChangesAsync(cancellationToken);
    }
}
