using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The options of one <c>AddChronoAudit</c> call. Later phases add the sections store, activity log, exports, calendar and anchors.</summary>
public sealed class ChronoAuditOptions
{
    /// <summary>The options of the audit pipeline (section <c>ChronoAudit:Audit</c>).</summary>
    public AuditTrailOptions Audit { get; } = new();

    /// <summary>The settings of the anchor worker (section <c>ChronoAudit:Anchors</c>).</summary>
    public AnchorOptions Anchors { get; } = new();

    /// <summary>Which field names are secrets (masked in extracts and activity bodies). Defaults only for now; the configuration section is read when the activity log needs it.</summary>
    public MaskingOptions Masking { get; } = new();

    /// <summary>Which audited fields are personal data (section <c>ChronoAudit:PersonalData</c>).</summary>
    public PersonalDataOptions PersonalData { get; } = new();

    /// <summary>Where the store tables live (section <c>ChronoAudit:Store</c>).</summary>
    public ChronoAuditStoreOptions Store { get; } = new();

    /// <summary>The API activity log settings (section <c>ChronoAudit:ActivityLog</c>).</summary>
    public ActivityLogOptions ActivityLog { get; } = new();

    /// <summary>The data lag settings (section <c>ChronoAudit:DataLag</c>).</summary>
    public DataLagOptions DataLag { get; } = new();

    /// <summary>The consistency worker settings (section <c>ChronoAudit:Consistency</c>).</summary>
    public ConsistencyOptions Consistency { get; } = new();

    /// <summary>The background export settings (section <c>ChronoAudit:Exports</c>).</summary>
    public ExportOptions Exports { get; } = new();

    /// <summary>The calendar of the calendar columns and time grains (section <c>ChronoAudit:Calendar</c>).</summary>
    public CalendarOptions Calendar { get; } = new();

    /// <summary>Keys of earlier key segments (<c>ChronoAudit:PreviousHmacKeys</c>), needed to verify rows hashed before a rotation.</summary>
    public IList<byte[]> PreviousHmacKeys { get; } = new List<byte[]>();

    /// <summary>Hex SHA-256 hashes of keys that must be refused outside development (keys that were ever published).</summary>
    public IReadOnlyCollection<string> RefusedKeySha256 { get; set; } = [];
}

/// <summary>What one <c>AddChronoAudit</c> call registered: the chain key, the shared options and the audited contexts. A later call must agree with it.</summary>
internal sealed class ChronoAuditRegistry
{
    private readonly List<(Type ContextType, string Module)> _modules = [];

    public required byte[] Key { get; init; }

    public required ChronoAuditOptions Options { get; init; }

    public bool HashWorkersRequested { get; set; }

    public bool ActivityLogAdded { get; set; }

    public bool TimelineAdded { get; set; }

    public List<RegisteredAnalyticsSubject> AnalyticsSubjects { get; } = [];

    public bool BuiltInAnalyticsRequested { get; set; }

    public IReadOnlyList<(Type ContextType, string Module)> Modules => _modules;

    public void Add(Type contextType, string module)
    {
        if (_modules.Any(m => m.ContextType == contextType))
        {
            throw new InvalidOperationException($"{contextType.Name} is already registered as an audited module.");
        }

        if (_modules.Any(m => string.Equals(m.Module, module, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The audit module name \"{module}\" is already registered; module names must be unique.");
        }

        _modules.Add((contextType, module));
    }
}

/// <summary>Registers the audited databases (modules) and, in later phases, the workers and the store.</summary>
public sealed partial class ChronoAuditBuilder
{
    internal ChronoAuditBuilder(IServiceCollection services, ChronoAuditRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    internal ChronoAuditRegistry Registry { get; }

    /// <summary>
    /// Adds one audited database. The context must already be registered with <c>AddDbContext</c> and derive from <see cref="AuditableDbContext"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The audited context.</typeparam>
    /// <param name="module">A short, stable name of the database (1 to 64 letters, digits, <c>-</c> or <c>_</c>); it appears in URLs and in anchors.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The module name is empty or not valid.</exception>
    /// <exception cref="InvalidOperationException">The context or the module name is already registered.</exception>
    public ChronoAuditBuilder AddModule<TDbContext>(string module)
        where TDbContext : AuditableDbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        if (!ModuleName().IsMatch(module))
        {
            throw new ArgumentException("A module name is 1 to 64 letters, digits, '-' or '_'.", nameof(module));
        }

        Registry.Add(typeof(TDbContext), module);
        Services.AddSingleton<IModuleAuditHistorySource>(sp => new AuditHistorySource<TDbContext>(module, sp.GetRequiredService<IServiceScopeFactory>()));
        Services.AddSingleton<IAuditChainSource>(sp => new AuditChainSource<TDbContext>(module, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<AuditChain>()));
        return this;
    }

    /// <summary>
    /// Says which context holds the store tables (the host added them with <c>ModelBuilder.ApplyChronoAuditStore</c> and created them with
    /// its own migration). The context need not be an <see cref="AuditableDbContext"/>.
    /// </summary>
    /// <typeparam name="TContext">The store context.</typeparam>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">A different store context was already chosen.</exception>
    public ChronoAuditBuilder UseStore<TContext>()
        where TContext : DbContext
    {
        var existing = Services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditStoreRegistration))?.ImplementationInstance as ChronoAuditStoreRegistration;
        if (existing is not null)
        {
            if (existing.ContextType != typeof(TContext))
            {
                throw new InvalidOperationException($"The ChronoAudit store is already set to {existing.ContextType.Name}; it cannot also be {typeof(TContext).Name}.");
            }

            return this;
        }

        Services.AddSingleton(new ChronoAuditStoreRegistration(typeof(TContext)));
        return this;
    }

    /// <summary>
    /// Registers the API activity log: the stores for request log rows and mutation intents, the masking services and the options.
    /// Needs <see cref="UseStore{TContext}"/>. The HTTP middleware that fills the log is in the ASP.NET Core package.
    /// </summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">No store context was chosen.</exception>
    public ChronoAuditBuilder AddActivityLog()
    {
        var registration = Services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditStoreRegistration))?.ImplementationInstance as ChronoAuditStoreRegistration
            ?? throw new InvalidOperationException("Call UseStore<TContext>() before AddActivityLog().");
        if (Services.Any(d => d.ServiceType == typeof(IApiRequestLogStore)))
        {
            return this;
        }

        Registry.Options.ActivityLog.Validate();
        Services.AddSingleton(Registry.Options.ActivityLog);
        Services.AddSingleton(sp => new BodyMasker(sp.GetRequiredService<SensitiveDataMasker>(), sp.GetRequiredService<MaskingOptions>()));
        Services.AddSingleton<IApiRequestLogStore>(sp => new ResilientApiRequestLogStore(
            new EfApiRequestLogStore(registration, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IChronoAuditCalendar>()),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        Services.AddSingleton<IApiMutationIntentStore>(sp => new EfApiMutationIntentStore(registration, sp.GetRequiredService<IServiceScopeFactory>()));
        Services.AddSingleton<StoreMarkerReader>();
        Services.AddSingleton(sp => new StoreChain<ApiRequestLog>(
            sp.GetRequiredService<AuditKeyRing>(),
            sp.GetRequiredService<StoreMarkerReader>(),
            StoreChainModules.Activity,
            query => query.Include(x => x.Body),
            StoreLayouts.Fields,
            (row, index, previous, hash) => row.SetHash(index, previous, hash),
            sp.GetRequiredService<ILogger<StoreChain<ApiRequestLog>>>()));
        Services.AddSingleton(sp => new StoreChain<ApiMutationIntent>(
            sp.GetRequiredService<AuditKeyRing>(),
            sp.GetRequiredService<StoreMarkerReader>(),
            StoreChainModules.Intents,
            query => query,
            StoreLayouts.Fields,
            (row, index, previous, hash) => row.SetHash(index, previous, hash),
            sp.GetRequiredService<ILogger<StoreChain<ApiMutationIntent>>>()));
        Services.AddSingleton<IAuditChainSource>(sp => new StoreChainSource<ApiRequestLog>(
            sp.GetRequiredService<StoreChain<ApiRequestLog>>(), registration, sp.GetRequiredService<IServiceScopeFactory>()));
        Services.AddSingleton<IAuditChainSource>(sp => new StoreChainSource<ApiMutationIntent>(
            sp.GetRequiredService<StoreChain<ApiMutationIntent>>(), registration, sp.GetRequiredService<IServiceScopeFactory>()));
        Services.AddSingleton<IActivitySearch>(sp => new ActivitySearchService(
            registration, Registry, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ActivityLogOptions>(), sp.GetRequiredService<TimeProvider>()));
        Registry.ActivityLogAdded = true;
        if (Registry.HashWorkersRequested)
        {
            Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ActivityHashWorker>());
        }

        return this;
    }

    /// <summary>Registers an analytics subject: its generic definition and where its rows come from.</summary>
    /// <typeparam name="TRow">The row type.</typeparam>
    /// <param name="subject">The subject.</param>
    /// <param name="source">Where the rows come from; it must apply the access scope.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">A subject with the same key is already registered.</exception>
    public ChronoAuditBuilder AddAnalyticsSubject<TRow>(AnalyticsSubject<TRow> subject, AnalyticsSource<TRow> source)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(source);
        if (Registry.AnalyticsSubjects.Any(s => string.Equals(s.Key, subject.Key, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"An analytics subject with the key '{subject.Key}' is already registered.");
        }

        Registry.AnalyticsSubjects.Add(new RegisteredAnalyticsSubject<TRow>(subject, source));
        return this;
    }

    /// <summary>
    /// Adds the analytics subjects over ChronoAudit's own data: <c>audit:{module}</c> for every audited module (All scope only) and
    /// <c>activity</c> when <see cref="AddActivityLog"/> was called. They are built when the analytics service is first created, so the
    /// order of the other builder calls does not matter.
    /// </summary>
    /// <returns>The builder.</returns>
    public ChronoAuditBuilder AddBuiltInAnalytics()
    {
        Registry.BuiltInAnalyticsRequested = true;
        return this;
    }

    /// <summary>
    /// Adds <see cref="ISavedReportService"/>: reports a user saves, shares with a group and runs again. Needs <see cref="UseStore{TContext}"/>
    /// (the <c>SavedReports</c> table comes with <c>ApplyChronoAuditStore</c>); it runs the reports through the analytics service.
    /// </summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">No store context was chosen.</exception>
    public ChronoAuditBuilder AddSavedReports()
    {
        var registration = Services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditStoreRegistration))?.ImplementationInstance as ChronoAuditStoreRegistration
            ?? throw new InvalidOperationException("Call UseStore<TContext>() before AddSavedReports().");
        Services.TryAddSingleton<ISavedReportService>(sp => new SavedReportService(
            registration,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IAnalyticsService>(),
            sp.GetRequiredService<IAnalyticsPlanner>(),
            sp.GetRequiredService<TimeProvider>()));
        return this;
    }

    /// <summary>
    /// Adds <see cref="IExportService"/> and the built-in export producers (<c>analytics</c>, <c>extract</c>, <c>audit</c>, <c>activity</c>).
    /// Needs <see cref="UseStore{TContext}"/> (the <c>ExportJobs</c> table comes with <c>ApplyChronoAuditStore</c>). The jobs are executed by
    /// the worker that <see cref="AddExportWorker"/> adds; run it on one or more nodes. Settings: section <c>ChronoAudit:Exports</c>.
    /// </summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">No store context was chosen, or an option is out of range.</exception>
    public ChronoAuditBuilder AddExports()
    {
        var registration = Services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditStoreRegistration))?.ImplementationInstance as ChronoAuditStoreRegistration
            ?? throw new InvalidOperationException("Call UseStore<TContext>() before AddExports().");
        if (Services.Any(d => d.ServiceType == typeof(IExportService)))
        {
            return this;
        }

        var options = Registry.Options.Exports;
        options.Validate();
        Services.AddSingleton(options);
        Services.AddSingleton<ExportFileStore>();
        Services.AddSingleton<IExportService>(sp => new ExportService(
            registration,
            sp.GetRequiredService<ChronoAuditRegistry>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            sp.GetRequiredService<ExportFileStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ExportService>>()));
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportProducer, AnalyticsExportProducer>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportProducer, ExtractExportProducer>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportProducer, AuditExportProducer>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportProducer, ActivityExportProducer>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportProducer, TimelineExportProducer>());
        return this;
    }

    /// <summary>
    /// Runs the background worker that executes the export jobs: it claims a job with a lease, writes the file in batches with checkpoints,
    /// and deletes old files. Run it on one or more nodes; two workers never own one job. Needs <see cref="AddExports"/>.
    /// </summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException"><see cref="AddExports"/> was not called.</exception>
    public ChronoAuditBuilder AddExportWorker()
    {
        var registration = Services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditStoreRegistration))?.ImplementationInstance as ChronoAuditStoreRegistration;
        if (registration is null || !Services.Any(d => d.ServiceType == typeof(IExportService)))
        {
            throw new InvalidOperationException("Call UseStore<TContext>() and AddExports() before AddExportWorker().");
        }

        if (Services.Any(d => d.ServiceType == typeof(ExportJobWorker)))
        {
            return this;
        }

        Services.AddSingleton(sp => new ExportJobWorker(
            registration,
            sp.GetRequiredService<ChronoAuditRegistry>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ExportOptions>(),
            sp.GetRequiredService<ExportFileStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ExportJobWorker>>()));
        Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ExportJobWorker>());
        return this;
    }

    /// <summary>
    /// Adds <see cref="ITimelineWriter"/> and <see cref="ITimelineReader"/>: the host records business events (idempotent by source event id) and ChronoAudit keeps them as
    /// an append-only timeline. Needs <see cref="UseStore{TContext}"/> (the <c>TimelineEvents</c> and <c>TimelineParticipants</c> tables
    /// come with <c>ApplyChronoAuditStore</c>; <c>CreateChronoAuditStoreGuards</c> guards the events table against change).
    /// </summary>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">No store context was chosen.</exception>
    public ChronoAuditBuilder AddTimeline()
    {
        var registration = Services.FirstOrDefault(d => d.ServiceType == typeof(ChronoAuditStoreRegistration))?.ImplementationInstance as ChronoAuditStoreRegistration
            ?? throw new InvalidOperationException("Call UseStore<TContext>() before AddTimeline().");
        Registry.TimelineAdded = true;
        Services.TryAddSingleton<ITimelineWriter>(sp => new TimelineWriter(
            registration, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IChronoAuditCalendar>(), sp.GetRequiredService<TimeProvider>()));
        Services.TryAddSingleton<IEvidenceService>(sp => new EvidenceService(
            sp.GetRequiredService<ChronoAuditRegistry>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ITimelineReader>(), sp.GetRequiredService<AuditChain>(),
            sp.GetRequiredService<SensitiveDataMasker>(), sp.GetRequiredService<PersonalDataMasking>(), sp.GetRequiredService<TimeProvider>()));
        Services.TryAddSingleton<ITimelineReader>(sp => new TimelineReader(
            registration, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<SensitiveDataMasker>(), sp.GetRequiredService<PersonalDataMasking>(), sp));
        return this;
    }

    /// <summary>
    /// Adds <see cref="IChronoAuditDataLag"/>: how old the oldest not-yet-delivered data is, asked from the host's
    /// <see cref="IChronoAuditDataLagSource"/> registrations and remembered for a few seconds (section <c>ChronoAudit:DataLag</c>).
    /// </summary>
    /// <returns>The builder.</returns>
    public ChronoAuditBuilder AddDataLag()
    {
        var options = Registry.Options.DataLag;
        options.Validate();
        Services.TryAddSingleton(options);
        Services.TryAddSingleton<IChronoAuditDataLag, DataLagReader>();
        return this;
    }

    /// <summary>
    /// Runs the host's <see cref="IConsistencyCheck"/>s every few hours and reports drift that outlives a grace wait (section
    /// <c>ChronoAudit:Consistency</c>). The worker never repairs anything.
    /// </summary>
    /// <returns>The builder.</returns>
    public ChronoAuditBuilder AddConsistencyWorker()
    {
        if (Services.Any(d => d.ServiceType == typeof(ConsistencyWorker)))
        {
            return this;
        }

        var options = Registry.Options.Consistency;
        options.Validate();
        Services.AddSingleton(options);
        Services.AddSingleton<ConsistencyWorker>();
        Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConsistencyWorker>());
        return this;
    }

    /// <summary>
    /// Adds a host export producer. Its <see cref="IExportProducer.Key"/> is what a request names; a key that a built-in producer already uses
    /// is not replaced (the built-in one is found first). The producer is resolved from a new service scope for every request and every
    /// batch, so it may take scoped services such as the host's database context.
    /// </summary>
    /// <typeparam name="TProducer">The producer type.</typeparam>
    /// <returns>The builder.</returns>
    public ChronoAuditBuilder AddExportProducer<TProducer>()
        where TProducer : class, IExportProducer
    {
        Services.AddScoped<IExportProducer, TProducer>();
        return this;
    }

    /// <summary>Runs the background worker that hashes the audit rows of every registered module. Run it on one node of a multi-instance deployment.</summary>
    /// <returns>The builder.</returns>
    public ChronoAuditBuilder AddHashWorkers()
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RegisteredAuditHashService>());
        Registry.HashWorkersRequested = true;
        if (Registry.ActivityLogAdded)
        {
            Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ActivityHashWorker>());
        }

        return this;
    }

    /// <summary>Runs the worker that appends the head of every chain to the anchor files and re-verifies the newest rows.</summary>
    /// <returns>The builder.</returns>
    public ChronoAuditBuilder AddAnchorWorker()
    {
        Services.TryAddSingleton(sp => new AuditAnchorLog(
            sp.GetRequiredService<AuditKeyRing>(),
            Registry.Options.Anchors.ResolvedDirectory,
            sp.GetRequiredService<ILogger<AuditAnchorLog>>()));
        Services.TryAddSingleton(Registry.Options.Anchors);
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AuditAnchorWorker>());
        return this;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleName();
}
