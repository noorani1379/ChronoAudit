using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The scoped implementation of <see cref="IAuditLogWriter"/> and <see cref="IAuditLogAccessor"/>: rows are held in memory until the context saves them.</summary>
/// <param name="correlationContext">Supplies the correlation id of the staged rows.</param>
/// <param name="currentUserService">The actor whose address and session are added to the metadata; optional.</param>
public sealed class ScopedAuditLogManager(
    ICorrelationContext correlationContext,
    IAuditActor? currentUserService = null) : IAuditLogWriter, IAuditLogAccessor
{
    private readonly List<AuditLogEntry> _pendingLogs = [];

    /// <inheritdoc />
    public Task AppendHashedAsync(
        string actorUserId, string action, string resourceType,
        string resourceId, object? metadata = null, CancellationToken cancellationToken = default)
    {
        // OccurredAtUtc and CalendarDate are stamped by the context that saves the row, from its clock and calendar.
        _pendingLogs.Add(new AuditLogEntry
        {
            CorrelationId = correlationContext.CorrelationId,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Metadata = BuildMetadata(metadata),
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AuditLogEntry> GetPendingLogs() => _pendingLogs.AsReadOnly();

    /// <inheritdoc />
    public void Clear() => _pendingLogs.Clear();

    // Adds the caller's Ip and SessionId when the metadata is a JSON object (or absent) and does not carry them already.
    // A non-object payload (string, array) and keys the caller set are never touched.
    private string? BuildMetadata(object? metadata)
    {
        var node = metadata is null ? new JsonObject() : JsonSerializer.SerializeToNode(metadata);
        if (node is JsonObject obj)
        {
            if (!obj.ContainsKey("Ip") && !string.IsNullOrWhiteSpace(currentUserService?.UserIp))
            {
                obj["Ip"] = currentUserService!.UserIp;
            }

            if (!obj.ContainsKey("SessionId") && !string.IsNullOrWhiteSpace(currentUserService?.SessionId))
            {
                obj["SessionId"] = currentUserService!.SessionId;
            }

            if (metadata is null && obj.Count == 0)
            {
                return null;
            }
        }

        return node?.ToJsonString();
    }
}
