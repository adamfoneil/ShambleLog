using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ShambleLog.Core;

internal sealed class LogEntryWriter(LogEntryQueue queue, IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stoppingToken))
            {
                queue.SuppressLogging = true;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<ILoggingDbContext>();
                    context.LogEntries.Add(entry);
                    await context.SaveChangesAsync(stoppingToken);
                }
                finally
                {
                    queue.SuppressLogging = false;
                }
            }
        }
        catch (Exception ex)
        {
            queue.Complete(ex);
            Console.Error.WriteLine($"ShambleLog: failed to persist log entries: {ex}");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        if (ExecuteTask is not null)
            await ExecuteTask.WaitAsync(cancellationToken);
    }
}
