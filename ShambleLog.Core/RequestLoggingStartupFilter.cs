using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ShambleLog.Core;

internal sealed class RequestLoggingStartupFilter(ILogger<RequestLoggingStartupFilter> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var start = Stopwatch.GetTimestamp();
            var method = context.Request.Method;
            var path = context.Request.PathBase.Add(context.Request.Path).Value;
            var failed = false;
            try
            {
                await nextMiddleware(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                var status = failed && !context.Response.HasStarted
                    ? StatusCodes.Status500InternalServerError
                    : context.Response.StatusCode;
                logger.LogInformation("HTTP {Method} {Path} responded {StatusCode} in {DurationMs} ms",
                    method, path, status, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        });
        next(app);
    };
}
