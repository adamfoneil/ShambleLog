using Microsoft.Extensions.DependencyInjection;

namespace ShambleLog.RCL;

public sealed class ScopedQueryRunner(IServiceScopeFactory scopeFactory)
{
    public async Task<TResult> ExecuteAsync<TQuery, TResult>(Func<TQuery, Task<TResult>> execute) where TQuery : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await execute(scope.ServiceProvider.GetRequiredService<TQuery>());
    }
}
