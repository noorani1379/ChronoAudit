# ChronoAudit.AspNetCore

The HTTP side of [ChronoAudit](https://github.com/noorani1379/ChronoAudit) for ASP.NET Core 8 and 10 (minimal APIs and MVC). Install it next to `ChronoAudit.EntityFrameworkCore`.

## What is in it

- `AddChronoAuditAspNetCore(...)`: the audit actor and correlation id from the request (claims and `X-Correlation-Id`), and the access scope (All, Group, Own) from claims. Every part can be replaced.
- `UseChronoAuditActivityLog()`: one activity row per API call with the masked body of write calls; place it before authentication.
- `AddChronoAuditMutationIntents()` and `WithChronoAuditMutationIntents()`: record each write request (who, what, SHA-256 of the body) before the handler runs; a write that cannot be recorded is refused with `503`.
- `MapChronoAudit("policy")`: history, audit search and verify, activity log, analytics, saved reports, exports, extract and timeline endpoints, each group also mappable on its own. A blank policy name throws; there is no unauthenticated mode.
- `X-Data-Lag-Seconds` on read calls, `ChronoAuditHttpHeaders.ExposedToBrowsers` for your CORS policy, and `application/problem+json` errors with stable codes.

## Example

```csharp
using ChronoAudit.AspNetCore;

public static class WebStartup
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddChronoAuditAspNetCore(configuration);   // reads ChronoAudit:AspNetCore:* (claim names, correlation header)
        services.AddChronoAuditMutationIntents();
        services.AddAuthorizationBuilder().AddPolicy("chronoaudit", p => p.RequireAuthenticatedUser());
    }

    public static void Configure(WebApplication app)
    {
        app.UseChronoAuditActivityLog();   // before UseAuthentication, so refused calls are logged too
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapChronoAudit("chronoaudit");
    }
}
```

The audit services themselves (`AddChronoAudit(...).AddModule<T>(...)`) come from `ChronoAudit.EntityFrameworkCore`; see the [getting started guide](https://github.com/noorani1379/ChronoAudit/blob/main/docs/guide/getting-started.md).

Documentation: [github.com/noorani1379/ChronoAudit](https://github.com/noorani1379/ChronoAudit).
