namespace ShambleLog.Core.Middleware;

public static class EFCoreMiddlewareExtensions
{
    /// <summary>
    /// Records executed and failed EF Core commands in <see cref="IEFCoreLoggingDbContext.EFCoreEntries"/>.
    /// </summary>
    public static ShambleLogBuilder TrackEFCoreQueries(this ShambleLogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseForCategory(EFCoreMiddleware.Category, ef => ef.Use<EFCoreMiddleware>());
    }
}
