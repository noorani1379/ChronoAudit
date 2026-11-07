using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Records an administrative action (a viewed body, an extract, a verification run) in the audit table, in a unit of work of its own.
/// Callers record first and release the result only when this succeeded: no record, no result.
/// </summary>
public interface IAuditRecorder
{
    /// <summary>Writes one audit row into the first audited module and saves it.</summary>
    /// <param name="actorUserId">Who did it.</param>
    /// <param name="action">What was done (a member of <see cref="AuditActions"/> or the host's own).</param>
    /// <param name="resourceType">The kind of thing it was done to.</param>
    /// <param name="resourceId">The identity of that thing.</param>
    /// <param name="metadata">Optional details, serialized to JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Success, or <c>ChronoAudit.AuditUnavailable</c> (Unavailable) when no audited module is registered or the row could not be saved.</returns>
    Task<ChronoAuditResult> RecordAsync(string actorUserId, string action, string resourceType, string resourceId, object? metadata, CancellationToken cancellationToken);
}

internal sealed class AuditRecorder(ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory) : IAuditRecorder
{
    private static readonly ChronoAuditError Unavailable = ChronoAuditError.Unavailable(
        "ChronoAudit.AuditUnavailable", "The action could not be recorded in the audit table, so it was not carried out; try again.");

    public async Task<ChronoAuditResult> RecordAsync(string actorUserId, string action, string resourceType, string resourceId, object? metadata, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (registry.Modules.Count == 0)
        {
            return ChronoAuditResult.Failure(Unavailable);
        }

        using var scope = scopeFactory.CreateScope();
        try
        {
            var audit = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(registry.Modules[0].ContextType);
            await scope.ServiceProvider.GetRequiredService<IAuditLogWriter>().AppendHashedAsync(actorUserId, action, resourceType, resourceId, metadata, cancellationToken);
            await audit.SaveChangesAsync(cancellationToken);
            return ChronoAuditResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            scope.ServiceProvider.GetRequiredService<IAuditLogAccessor>().Clear();
            return ChronoAuditResult.Failure(Unavailable);
        }
    }
}
