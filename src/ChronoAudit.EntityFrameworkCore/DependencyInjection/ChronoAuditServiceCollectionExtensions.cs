using System.Globalization;
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Entry points for registering ChronoAudit.</summary>
public static class ChronoAuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit services: the options, the wake-up channel, the scoped writer for hand-written rows, and the chain key read from
    /// <c>ChronoAudit:HmacKey</c> (a missing or weak key stops the host outside Development and Testing, see <see cref="ChronoAuditKey"/>).
    /// The host must also register an <see cref="IAuditActor"/> and an <see cref="ICorrelationContext"/> (scoped) and its contexts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Where the HMAC key and the <c>ChronoAudit:Audit</c> section are read from.</param>
    /// <param name="environment">Decides whether the public development key is allowed.</param>
    /// <param name="configure">Overrides for the options, applied after the configuration section.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">The key is missing or weak, or a previous call registered a different key or different options.</exception>
    public static ChronoAuditBuilder AddChronoAudit(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<ChronoAuditOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new ChronoAuditOptions();
        ReadAuditSection(configuration.GetSection(AuditTrailOptions.Section), options.Audit);
        ReadAnchorSection(configuration.GetSection(AnchorOptions.Section), options.Anchors);
        ReadPersonalDataSection(configuration.GetSection(PersonalDataOptions.Section), options.PersonalData);
        ReadOperationalSections(configuration, options);
        configure?.Invoke(options);

        var key = ChronoAuditKey.Resolve(
            configuration[ChronoAuditKey.ConfigurationKey],
            allowDevelopmentKey: environment.IsDevelopment() || environment.IsEnvironment("Testing"),
            options.RefusedKeySha256);

        // Keys of earlier segments: each must be a real key (never the development key), and the current key is not listed twice.
        var current = System.Text.Encoding.UTF8.GetBytes(configuration[ChronoAuditKey.ConfigurationKey] ?? string.Empty);
        foreach (var child in configuration.GetSection("ChronoAudit:PreviousHmacKeys").GetChildren())
        {
            var previous = ChronoAuditKey.Resolve(child.Value, allowDevelopmentKey: false, options.RefusedKeySha256);
            if (!previous.AsSpan().SequenceEqual(current))
            {
                options.PreviousHmacKeys.Add(previous);
            }
        }
        return services.AddChronoAudit(key, options);
    }

    /// <summary>The same registration with a key you already resolved (a vault, a test) and default options.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hmacKey">The chain key (see <see cref="ChronoAuditKey.MinimumBytes"/>).</param>
    /// <returns>The builder.</returns>
    public static ChronoAuditBuilder AddChronoAudit(this IServiceCollection services, byte[] hmacKey)
        => services.AddChronoAudit(hmacKey, (ChronoAuditOptions?)null);

    /// <summary>The same registration with a key you already resolved (a vault, a test).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hmacKey">The chain key (see <see cref="ChronoAuditKey.MinimumBytes"/>).</param>
    /// <param name="options">The options instance to share; null uses a default one.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">A previous call registered a different key or a different options instance.</exception>
    public static ChronoAuditBuilder AddChronoAudit(this IServiceCollection services, byte[] hmacKey, ChronoAuditOptions? options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hmacKey);

        // A second call extends the registry the workers will read; it may not silently change what the first call decided.
        var registry = services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditRegistry))?.ImplementationInstance as ChronoAuditRegistry;
        if (registry is null)
        {
            registry = new ChronoAuditRegistry { Key = hmacKey, Options = options ?? new ChronoAuditOptions() };
            registry.Options.Anchors.Validate();
            services.AddSingleton(registry);
            services.AddSingleton(registry.Options);
            services.AddSingleton(registry.Options.Audit);
            services.AddSingleton<AuditSignalChannel>();
            services.AddSingleton(new AuditKeyRing(hmacKey, registry.Options.PreviousHmacKeys));
            services.AddSingleton(sp => new AuditChain(sp.GetRequiredService<AuditKeyRing>(), sp.GetService<ILogger<AuditChain>>()));
            services.AddSingleton<IAuditChainSources, AuditChainSources>();
            services.AddSingleton<IAuditHistory, AuditHistorySources>();
            services.AddSingleton<IAuditSearch, AuditSearchService>();
            services.AddSingleton<IAuditRecorder, AuditRecorder>();
            services.AddSingleton(registry.Options.Masking);
            services.AddSingleton(registry.Options.PersonalData);
            services.AddSingleton<SensitiveDataMasker>();
            services.AddSingleton<PersonalDataMasking>();
            services.AddSingleton<IExtractReader, ExtractReader>();
            services.AddScoped<IChangeReader, ChangeReader>();
            services.AddSingleton<AnalyticsService>();
            services.AddSingleton<IAnalyticsService>(sp => sp.GetRequiredService<AnalyticsService>());
            services.AddSingleton<IAnalyticsPlanner>(sp => sp.GetRequiredService<AnalyticsService>());
            services.AddSingleton<IActivityStatistics, ActivityStatisticsService>();
            services.TryAddSingleton<IPersonalDataRevealPolicy, RefuseAllRevealsPolicy>();
            services.AddSingleton<IChainMaintenance, ChainMaintenance>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ChronoAuditStartupLog>());
            services.TryAddSingleton(TimeProvider.System);
            registry.Options.Store.Validate();
            services.AddSingleton(registry.Options.Store);
            services.TryAddSingleton(registry.Options.Calendar.Create());
            services.AddScoped<ScopedAuditLogManager>();
            services.AddScoped<IAuditLogWriter>(sp => sp.GetRequiredService<ScopedAuditLogManager>());
            services.AddScoped<IAuditLogAccessor>(sp => sp.GetRequiredService<ScopedAuditLogManager>());
        }
        else
        {
            if (!hmacKey.AsSpan().SequenceEqual(registry.Key))
            {
                throw new InvalidOperationException("AddChronoAudit was already called with a different chain key; one host signs its audit chains with one key.");
            }

            if (options is not null && !ReferenceEquals(options, registry.Options))
            {
                throw new InvalidOperationException("AddChronoAudit was already called with a different ChronoAuditOptions instance; pass the same instance or omit it.");
            }
        }

        return new ChronoAuditBuilder(services, registry);
    }

    private static void ReadOperationalSections(IConfiguration configuration, ChronoAuditOptions options)
    {
        if (configuration[ChronoAuditStoreOptions.Section + ":Schema"] is { } schema)
        {
            options.Store.Schema = schema;
        }

        var calendar = configuration.GetSection(CalendarOptions.Section);
        if (calendar["Kind"] is { } kind)
        {
            options.Calendar.Kind = kind;
        }

        if (calendar["TimeZoneId"] is { } zone)
        {
            options.Calendar.TimeZoneId = zone;
        }

        var activity = configuration.GetSection(ActivityLogOptions.Section);
        if (int.TryParse(activity["HashIntervalSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
        {
            options.ActivityLog.HashIntervalSeconds = interval;
        }

        if (int.TryParse(activity["HashBatchSize"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var batch))
        {
            options.ActivityLog.HashBatchSize = batch;
        }

        if (int.TryParse(activity["IncompleteIntentAgeMinutes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age))
        {
            options.ActivityLog.IncompleteIntentAgeMinutes = age;
        }

        var prefixes = activity.GetSection("ExcludedPathPrefixes").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (prefixes.Count > 0)
        {
            options.ActivityLog.ExcludedPathPrefixes.Clear();
            foreach (var prefix in prefixes)
            {
                options.ActivityLog.ExcludedPathPrefixes.Add(prefix!);
            }
        }

        var exports = configuration.GetSection(ExportOptions.Section);
        if (exports["Directory"] is { Length: > 0 } exportDirectory)
        {
            options.Exports.Directory = exportDirectory;
        }

        if (int.TryParse(exports["RetentionDays"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention))
        {
            options.Exports.RetentionDays = retention;
        }

        if (int.TryParse(exports["LeaseSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lease))
        {
            options.Exports.LeaseSeconds = lease;
        }

        if (int.TryParse(exports["PollIntervalSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var poll))
        {
            options.Exports.PollIntervalSeconds = poll;
        }

        if (long.TryParse(exports["MaxRows"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRows))
        {
            options.Exports.MaxRows = maxRows;
        }

        if (int.TryParse(exports["MaxActivePerUser"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxActive))
        {
            options.Exports.MaxActivePerUser = maxActive;
        }

        if (int.TryParse(exports["BatchSize"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exportBatch))
        {
            options.Exports.BatchSize = exportBatch;
        }

        var lag = configuration.GetSection(DataLagOptions.Section);
        if (int.TryParse(lag["CacheSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cacheSeconds))
        {
            options.DataLag.CacheSeconds = cacheSeconds;
        }

        var consistency = configuration.GetSection(ConsistencyOptions.Section);
        if (int.TryParse(consistency["IntervalHours"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalHours))
        {
            options.Consistency.IntervalHours = intervalHours;
        }

        if (int.TryParse(consistency["GraceSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var graceSeconds))
        {
            options.Consistency.GraceSeconds = graceSeconds;
        }

        var masking = configuration.GetSection(MaskingOptions.Section);
        if (int.TryParse(masking["MaxBodyBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxBody))
        {
            options.Masking.MaxBodyBytes = maxBody;
        }

        var fragments = masking.GetSection("CodeIsSecretPathFragments").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (fragments.Count > 0)
        {
            options.Masking.CodeIsSecretPathFragments.Clear();
            foreach (var fragment in fragments)
            {
                options.Masking.CodeIsSecretPathFragments.Add(fragment!);
            }
        }
    }

    private static void ReadPersonalDataSection(IConfigurationSection section, PersonalDataOptions personalData)
    {
        foreach (var child in section.GetSection("NumberFields").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                personalData.NumberFields.Add(child.Value);
            }
        }

        foreach (var child in section.GetSection("TextFields").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                personalData.TextFields.Add(child.Value);
            }
        }
    }

    private static void ReadAnchorSection(IConfigurationSection section, AnchorOptions anchors)
    {
        if (section["Directory"] is { } directory)
        {
            anchors.Directory = directory;
        }

        if (int.TryParse(section["IntervalHours"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
        {
            anchors.IntervalHours = interval;
        }

        if (int.TryParse(section["StartupDelaySeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay))
        {
            anchors.StartupDelaySeconds = delay;
        }

        if (int.TryParse(section["VerifyWindowRows"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var window))
        {
            anchors.VerifyWindowRows = window;
        }
    }

    // The Binder package is not an allowed dependency, so the few scalar settings are read by hand.
    private static void ReadAuditSection(IConfigurationSection section, AuditTrailOptions audit)
    {
        if (section["SystemUserId"] is { } systemUserId)
        {
            audit.SystemUserId = systemUserId;
        }

        if (section["SystemDisplayName"] is { } systemDisplayName)
        {
            audit.SystemDisplayName = systemDisplayName;
        }

        if (section["DelegatedDisplayNameFormat"] is { } format)
        {
            audit.DelegatedDisplayNameFormat = format;
        }

        if (int.TryParse(section["BacklogWarnRows"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var backlog))
        {
            audit.BacklogWarnRows = backlog;
        }
    }
}
