using Microsoft.EntityFrameworkCore;

namespace ShambleLog.Core;

public static class InternalQueryTagExtensions
{
    public static IQueryable<T> TagAsShambleLogInternal<T>(this IQueryable<T> query) =>
        query.TagWith("ShambleLog Internal");
}
