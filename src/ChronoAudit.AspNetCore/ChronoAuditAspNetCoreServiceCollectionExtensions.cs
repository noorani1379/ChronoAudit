using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the ASP.NET Core defaults of ChronoAudit.</summary>
public static class ChronoAuditAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HTTP versions of the actor, the correlation context and the access scope, with <c>TryAdd</c>: a registration of the
    /// host always wins. Also registers the HTTP context accessor.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddChronoAuditAspNetCore(this IServiceCollection services)
        => AddCore(services, new ChronoAuditAspNetCoreOptions());

    /// <summary>Registers the defaults and lets the host change the options.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Changes the options.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddChronoAuditAspNetCore(this IServiceCollection services, Action<ChronoAuditAspNetCoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ChronoAuditAspNetCoreOptions();
        configure(options);
        return AddCore(services, options);
    }

    /// <summary>Registers the defaults and reads the string settings of the section <c>ChronoAudit:AspNetCore</c>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddChronoAuditAspNetCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ChronoAuditAspNetCoreOptions();
        var section = configuration.GetSection(ChronoAuditAspNetCoreOptions.Section);
        ReplaceList(section.GetSection("UserIdClaimTypes"), options.UserIdClaimTypes);
        ReplaceList(section.GetSection("NameClaimTypes"), options.NameClaimTypes);
        ReplaceList(section.GetSection("RoleClaimTypes"), options.RoleClaimTypes);
        options.GroupIdClaimType = Text(section, "GroupIdClaimType", options.GroupIdClaimType);
        options.SessionIdClaimType = Text(section, "SessionIdClaimType", options.SessionIdClaimType);
        options.ActAsUserIdClaimType = Text(section, "ActAsUserIdClaimType", options.ActAsUserIdClaimType);
        options.ScopeClaimType = Text(section, "ScopeClaimType", options.ScopeClaimType);
        options.CorrelationHeader = Text(section, "CorrelationHeader", options.CorrelationHeader);
        return AddCore(services, options);
    }

    private static string Text(IConfiguration section, string key, string fallback) => section[key] is { Length: > 0 } value ? value : fallback;

    private static void ReplaceList(IConfiguration section, IList<string> target)
    {
        var values = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (values.Count == 0)
        {
            return;
        }

        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static IServiceCollection AddCore(IServiceCollection services, ChronoAuditAspNetCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddSingleton(options);
        services.TryAddScoped<IAuditActor, HttpContextAuditActor>();
        services.TryAddScoped<ICorrelationContext, HttpCorrelationContext>();
        services.TryAddSingleton<IChronoAuditAccessScope, ClaimsAccessScope>();
        return services;
    }
}
