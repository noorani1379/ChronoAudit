using ChronoAudit.EntityFrameworkCore;

namespace ChronoAudit.IntegrationTests;

public sealed class FakeAuditActor : IAuditActor
{
    public string? UserId { get; set; } = "user-1";

    public string? DisplayName { get; set; } = "Ada";

    public string? UserIp { get; set; } = "10.0.0.7";

    public string? SessionId { get; set; } = "sess-1";

    public string? Role { get; set; }

    public string? RoleTitle { get; set; }

    public string? ActorUserId { get; set; }

    public bool IsDelegated { get; set; }

    public object? AuditMetadata { get; set; }
}

public sealed class FakeCorrelation(Guid id) : ICorrelationContext
{
    public Guid CorrelationId { get; } = id;
}

public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

