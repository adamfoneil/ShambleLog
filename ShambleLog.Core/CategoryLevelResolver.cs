using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

internal static class CategoryLevelResolver
{
    public static CategoryLevel? FindEffective(IEnumerable<CategoryLevel> rules, string category) =>
        rules.Where(item => !string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase)
                && category.StartsWith(item.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Category.Length)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? rules.Where(item => string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .FirstOrDefault();
}
