using Microsoft.Extensions.Logging;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core.Queries;

public static class CategorySummaryProjection
{
    public sealed record Summary(CategorySummaryQuery.Result Result, string DisplayCategory, LogLevel[] LiveLevelOptions);
    public sealed record Rule(CategoryLevel CategoryLevel, Summary[] Summaries);

    public static Rule[] Build(CategoryLevel[] rules, CategorySummaryQuery.Result[] summaries)
    {
        var groups = rules
            .Select(rule => new { Rule = rule, Summaries = new List<Summary>() })
            .ToArray();

        foreach (var summary in summaries.OrderBy(item => item.Category))
        {
            var effectiveRule = FindEffective(rules, summary.Category);
            var group = groups.FirstOrDefault(item => ReferenceEquals(item.Rule, effectiveRule));
            if (group is not null)
                group.Summaries.Add(new Summary(summary, GetDisplayCategory(summary.Category, group.Rule), GetLiveLevelOptions(group.Rule.Level)));
        }

        return [.. groups.Select(group => new Rule(group.Rule, group.Summaries.ToArray()))];
    }

    public static LogLevel[] GetLiveLevelOptions(LogLevel level) => level switch
    {
        LogLevel.Debug => [LogLevel.Information],
        LogLevel.Information => [LogLevel.Debug, LogLevel.Warning],
        LogLevel.Warning => [LogLevel.Information],
        _ => []
    };

    private static CategoryLevel? FindEffective(IEnumerable<CategoryLevel> rules, string category) =>
        rules.Where(item => !string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase)
                && category.StartsWith(item.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Category.Length)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? rules.Where(item => string.Equals(item.Category, "Default", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .FirstOrDefault();

    private static string GetDisplayCategory(string category, CategoryLevel rule) =>
        !string.Equals(rule.Category, "Default", StringComparison.OrdinalIgnoreCase)
            && category.Length > rule.Category.Length
            && category.StartsWith(rule.Category, StringComparison.OrdinalIgnoreCase)
                ? category[rule.Category.Length..].TrimStart('.')
                : category;
}
