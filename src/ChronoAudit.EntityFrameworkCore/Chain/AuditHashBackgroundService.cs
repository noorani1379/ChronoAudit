using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The instruments of the audit chain, all on the meter <c>ChronoAudit</c>.</summary>
internal static class AuditChainMetrics
{
    public static readonly Meter Meter = new(ChronoAuditMetrics.MeterName);

    public static readonly Histogram<int> HashedRows = Meter.CreateHistogram<int>("chronoaudit.audit.hashed_rows", description: "Rows hashed per round.");

    public static readonly Counter<long> HashFailures = Meter.CreateCounter<long>("chronoaudit.audit.hash_failures", description: "Hash rounds that failed.");

    private static readonly ConcurrentDictionary<string, long> Backlog = new(StringComparer.Ordinal);

    private static readonly ObservableGauge<long> BacklogGauge = Meter.CreateObservableGauge(
        "chronoaudit.audit.hash_backlog",
        () => Backlog.Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("module", kv.Key))).ToList(),
        description: "Audit rows waiting to be hashed.");

    public static void SetBacklog(string module, long pending)
    {
        _ = BacklogGauge;
        Backlog[module] = pending;
    }
}

/// <summary>
/// Extends the hash chain of every audit table after new audit rows were saved. It sleeps on <see cref="AuditSignalChannel"/> (no polling),
/// drains the backlog once at start, and processes each context in its own scope so one failing database never blocks the others.
/// Use <see cref="ChronoAuditBuilder.AddHashWorkers"/> to run it for the registered modules, or derive from it to supply the context list yourself.
/// </summary>
public class AuditHashBackgroundService : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly AuditChain _chain;
    private readonly AuditSignalChannel _signal;
    private readonly IReadOnlyList<(Type ContextType, string Module)> _modules;
    private readonly AuditTrailOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the worker.</summary>
    /// <param name="serviceProvider">Used to open one scope per context per round.</param>
    /// <param name="chain">The chain (it holds the key ring).</param>
    /// <param name="signal">The wake-up signal the contexts raise after saving audit rows.</param>
    /// <param name="modules">Every <see cref="AuditableDbContext"/> type whose audit table must be hashed, with its module name.</param>
    /// <param name="options">The knobs (backlog warning threshold).</param>
    /// <param name="logger">Where progress and failures are logged.</param>
    protected AuditHashBackgroundService(
        IServiceProvider serviceProvider,
        AuditChain chain,
        AuditSignalChannel signal,
        IReadOnlyList<(Type ContextType, string Module)> modules,
        AuditTrailOptions options,
        ILogger logger)
    {
        _serviceProvider = serviceProvider;
        _chain = chain;
        _signal = signal;
        _modules = modules;
        _options = options;
        _logger = logger;
    }

    /// <summary>The unhashed-row count above which a warning is logged. Read on every round, so a host can make it follow its live configuration. Default: <see cref="AuditTrailOptions.BacklogWarnRows"/>.</summary>
    protected virtual int BacklogWarnRows => _options.BacklogWarnRows;

    /// <summary>Hashes the pending rows of every module once. One failing module never blocks the others.</summary>
    /// <param name="stoppingToken">Cancellation.</param>
    /// <returns>False when at least one module failed and must be retried.</returns>
    public async Task<bool> DrainBacklogAsync(CancellationToken stoppingToken)
    {
        var allSucceeded = true;

        foreach (var (contextType, module) in _modules)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(contextType);
                await WarnOnBacklogAsync(dbContext, module, stoppingToken);
                var hashed = await _chain.HashAllPendingAsync(dbContext, BatchSize, stoppingToken, module);

                if (hashed > 0)
                {
                    AuditChainMetrics.HashedRows.Record(hashed, new KeyValuePair<string, object?>("module", module));
                    _logger.LogDebug("Hashed {HashedCount} audit rows for {Module}.", hashed, module);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                allSucceeded = false;
                AuditChainMetrics.HashFailures.Add(1, new KeyValuePair<string, object?>("module", module));
                _logger.LogError(ex, "Failed to hash audit rows for {Module}. Retrying in {RetrySeconds} seconds.", module, RetryDelay.TotalSeconds);
            }
        }

        return allSucceeded;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audit hash worker started for {ModuleCount} modules.", _modules.Count);

        try
        {
            var succeeded = await DrainBacklogAsync(stoppingToken);

            await using var signals = _signal.WaitAsync(stoppingToken).GetAsyncEnumerator(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (succeeded)
                {
                    if (!await signals.MoveNextAsync())
                    {
                        break;
                    }
                }
                else
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }

                succeeded = await DrainBacklogAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    // A growing pile of unhashed rows means the worker cannot keep up or keeps failing: say so before the evidence has a long unprotected tail.
    private async Task WarnOnBacklogAsync(AuditableDbContext dbContext, string module, CancellationToken stoppingToken)
    {
        var pending = await dbContext.AuditLogEntries.AsNoTracking().LongCountAsync(x => x.PayloadHash == null, stoppingToken);
        AuditChainMetrics.SetBacklog(module, pending);
        var limit = BacklogWarnRows;
        if (pending > limit)
        {
            _logger.LogWarning("Audit hash backlog: {PendingRows} unhashed rows in {Module} (limit {Limit}).", pending, module, limit);
        }
    }
}

/// <summary>The hash worker for the contexts registered with <see cref="ChronoAuditBuilder.AddModule{TDbContext}"/>.</summary>
internal sealed class RegisteredAuditHashService(
    IServiceProvider serviceProvider,
    AuditChain chain,
    AuditSignalChannel signal,
    ChronoAuditRegistry registry,
    AuditTrailOptions options,
    ILogger<RegisteredAuditHashService> logger)
    : AuditHashBackgroundService(serviceProvider, chain, signal, registry.Modules, options, logger);
