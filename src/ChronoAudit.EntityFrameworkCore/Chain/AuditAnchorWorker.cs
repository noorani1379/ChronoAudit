using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Once per <see cref="AnchorOptions.IntervalHours"/> writes the head of every chain to the anchor file outside the database
/// (<see cref="AuditAnchorLog"/>), then checks the old anchors against the database and re-verifies the newest
/// <see cref="AnchorOptions.VerifyWindowRows"/> rows of each chain. A broken chain, a missing anchored row or an edited anchor line is a
/// <c>Critical</c> log with structured properties (module, chain index, failure) and never the key or a row value.
/// </summary>
public sealed class AuditAnchorWorker(
    IAuditChainSources sources,
    AuditAnchorLog anchorLog,
    AnchorOptions options,
    TimeProvider clock,
    ILogger<AuditAnchorWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.IntervalHours));
        try
        {
            // Let the first hashing pass finish, so the first anchor is not "nothing hashed yet".
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.StartupDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>One round: anchor, then verify. A failing chain is logged and never stops the others.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the round is done.</returns>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var window = Math.Max(1, options.VerifyWindowRows);
        var now = clock.GetUtcNow();

        // Read before this round writes: the new anchors would only confirm themselves.
        var previous = anchorLog.ReadAll();
        if (previous.TamperedLines > 0)
        {
            logger.LogCritical("Audit anchor file has {TamperedLines} edited, cut or inserted lines (Module {Module}).", previous.TamperedLines, "anchor-file");
        }

        foreach (var module in sources.Modules)
        {
            var source = sources.Find(module);
            if (source is null)
            {
                continue;
            }

            try
            {
                var head = await source.GetHeadAsync(cancellationToken);
                if (head is null)
                {
                    continue;
                }

                var mine = previous.Anchors.Where(a => string.Equals(a.Module, module, StringComparison.Ordinal)).ToList();
                LogAnchorCheck(module, await source.VerifyAnchorsAsync(mine, cancellationToken));
                var verification = await source.VerifyRangeAsync(Math.Max(0, head.ChainIndex - window), int.MaxValue, cancellationToken);
                LogVerification(module, verification);
                anchorLog.TryAppend(module, head.ChainIndex, head.PayloadHash, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit anchoring failed for {Module}; it is tried again at the next interval.", module);
            }
        }
    }

    private void LogAnchorCheck(string module, AnchorCheck check)
    {
        if (check.Mismatches.Count > 0)
        {
            logger.LogCritical(
                "Audit anchors no longer match the database: Module {Module}, {MismatchCount} of {Checked} anchors, first chain index {FirstMismatchChainIndex}.",
                module,
                check.Mismatches.Count,
                check.Checked,
                check.Mismatches[0]);
        }
    }

    private void LogVerification(string module, AuditChainVerification verification)
    {
        if (!verification.IsValid)
        {
            logger.LogCritical(
                "Audit hash chain is broken: Module {Module}, first broken chain index {FirstBrokenChainIndex}, failure {Failure}.",
                module,
                verification.FailedAtChainIndex,
                verification.Failure);
        }
    }
}
