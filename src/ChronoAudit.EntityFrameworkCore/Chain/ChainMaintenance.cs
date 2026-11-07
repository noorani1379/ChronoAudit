using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Maintenance of the hash chains that changes them (as opposed to verifying them).</summary>
public interface IChainMaintenance
{
    /// <summary>
    /// Starts a new key segment: adds a rotation marker so the next hashing round hashes with the current key. The key that signed the
    /// chain so far must be in the key ring (<c>ChronoAudit:PreviousHmacKeys</c>), otherwise the chain cannot be verified later.
    /// </summary>
    /// <param name="module">The module name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Success, or <c>Chain.UnknownModule</c>, <c>Chain.NothingToRotate</c>, <c>Chain.RotationPending</c>, <c>Chain.UnknownKey</c>.</returns>
    Task<ChronoAuditResult> RotateKeyAsync(string module, CancellationToken cancellationToken);
}

internal sealed class ChainMaintenance(
    IServiceProvider services,
    ChronoAuditRegistry registry,
    IServiceScopeFactory scopeFactory,
    AuditChain chain,
    AuditTrailOptions options,
    TimeProvider clock,
    IChronoAuditCalendar calendar,
    AuditSignalChannel signal) : IChainMaintenance
{
    public async Task<ChronoAuditResult> RotateKeyAsync(string module, CancellationToken cancellationToken)
    {
        if (string.Equals(module, StoreChainModules.Activity, StringComparison.Ordinal))
        {
            return await RotateStoreChainAsync(services.GetService<StoreChain<ApiRequestLog>>(), module, cancellationToken);
        }

        if (string.Equals(module, StoreChainModules.Intents, StringComparison.Ordinal))
        {
            return await RotateStoreChainAsync(services.GetService<StoreChain<ApiMutationIntent>>(), module, cancellationToken);
        }

        var entry = registry.Modules.FirstOrDefault(m => string.Equals(m.Module, module, StringComparison.OrdinalIgnoreCase));
        if (entry.ContextType is null)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.NotFound("Chain.UnknownModule", "No audited module has this name."));
        }

        using var scope = scopeFactory.CreateScope();
        var db = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(entry.ContextType);
        var result = await chain.RotateKeyAsync(db, entry.Module, options.SystemUserId, clock, calendar, cancellationToken);
        if (result.IsSuccess)
        {
            signal.NotifyNewAudit();
        }

        return result;
    }

    // The store chains keep their rotation markers in the audit table of the first audited module; the store chain lock is held
    // meanwhile, so no row is hashed with the old key after the boundary was read.
    private async Task<ChronoAuditResult> RotateStoreChainAsync<TRow>(StoreChain<TRow>? storeChain, string module, CancellationToken cancellationToken)
        where TRow : class
    {
        var storeRegistration = services.GetService<ChronoAuditStoreRegistration>();
        if (storeChain is null || storeRegistration is null || registry.Modules.Count == 0)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.NotFound("Chain.UnknownModule", "No audited module has this name."));
        }

        using var scope = scopeFactory.CreateScope();
        var storeDb = (DbContext)scope.ServiceProvider.GetRequiredService(storeRegistration.ContextType);
        var auditDb = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(registry.Modules[0].ContextType);

        async Task<ChronoAuditResult> Body()
        {
            if (await auditDb.AuditLogEntries.AsNoTracking().AnyAsync(
                    x => x.PayloadHash == null && x.Action == AuditActions.KeyRotated && x.ResourceId == module, cancellationToken))
            {
                return ChronoAuditResult.Failure(ChronoAuditError.Conflict("Chain.RotationPending", "A key rotation is already waiting to be hashed."));
            }

            var head = await storeChain.FindHeadKeyAsync(storeDb, cancellationToken);
            if (head is null)
            {
                return ChronoAuditResult.Failure(ChronoAuditError.Conflict(
                    "Chain.NothingToRotate", "The chain has no hashed row yet; the current key will be used from the start."));
            }

            if (head.Value.Fingerprint is null)
            {
                return ChronoAuditResult.Failure(ChronoAuditError.Conflict(
                    "Chain.UnknownKey", "No configured key verifies the newest hashed row; add the old key to ChronoAudit:PreviousHmacKeys."));
            }

            if (string.Equals(head.Value.Fingerprint, chain.KeyRing.CurrentFingerprint, StringComparison.Ordinal))
            {
                return ChronoAuditResult.Failure(ChronoAuditError.Conflict("Chain.NothingToRotate", "The chain already uses the current key."));
            }

            var now = clock.GetUtcNow();
            var parts = calendar.PartsOf(now);
            var marker = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = options.SystemUserId,
                Action = AuditActions.KeyRotated,
                ResourceType = "ChronoAudit.Chain",
                ResourceId = module,
                Metadata = new System.Text.Json.Nodes.JsonObject
                {
                    [ChainMarker.Fields[0]] = head.Value.Fingerprint,
                    [ChainMarker.Fields[1]] = chain.KeyRing.CurrentFingerprint,
                    [ChainMarker.Fields[2]] = head.Value.ChainIndex,
                }.ToJsonString(),
            };
            marker.StampTime(now, (parts.Year * 10000) + (parts.Month * 100) + parts.Day);
            auditDb.AuditLogEntries.Add(marker);
            await auditDb.SaveChangesAsync(cancellationToken);
            return ChronoAuditResult.Success();
        }

        ChronoAuditResult result;
        if (storeDb.Database.IsRelational())
        {
            await using var transaction = await storeDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            await storeChain.AcquireLockAsync(storeDb, cancellationToken);
            result = await Body();
            if (result.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        else
        {
            result = await Body();
        }

        if (result.IsSuccess)
        {
            signal.NotifyNewAudit();
        }

        return result;
    }
}

/// <summary>Logs the fingerprint of the chain key once at start (never the key), so a changed key is visible in the log, and warns about unknown providers.</summary>
internal sealed class ChronoAuditStartupLog(
    AuditKeyRing keyRing, ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory, ILogger<ChronoAuditStartupLog> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("ChronoAudit chain key fingerprint {Fingerprint}", keyRing.CurrentFingerprint);

        using var scope = scopeFactory.CreateScope();
        foreach (var (contextType, _) in registry.Modules)
        {
            var db = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(contextType);
            if (ChronoAuditDialect.For(db).Kind == ProviderKind.Unknown)
            {
                logger.LogWarning(
                    "ChronoAudit does not know the database provider {Provider}: filtered indexes use double quotes, the chain lock is not taken, so run only one hash worker.",
                    db.Database.ProviderName);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
