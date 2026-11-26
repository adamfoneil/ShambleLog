using Microsoft.AspNetCore.Http;
using ShambleLog.Core.Entities;

namespace ShambleLog.Core;

public sealed class IdentityUserEnricher(IHttpContextAccessor accessor)
{
    internal void Enrich(LogEntry entry)
    {
        entry.Properties.Remove("IdentityUser");
        var identity = accessor.HttpContext?.User.Identity;
        if (identity?.IsAuthenticated == true && identity.Name is { } name)
            entry.Properties["IdentityUser"] = name;
    }
}
