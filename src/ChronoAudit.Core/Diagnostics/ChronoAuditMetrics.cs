namespace ChronoAudit;

/// <summary>
/// The name of the one meter every ChronoAudit package reports to. Each package creates its own <c>internal static readonly Meter</c>
/// with this name; listeners subscribe by name, so all packages appear under one meter. Instrument names follow
/// <c>chronoaudit.&lt;area&gt;.&lt;name&gt;</c>.
/// </summary>
public static class ChronoAuditMetrics
{
    /// <summary>The meter name: <c>ChronoAudit</c>.</summary>
    public const string MeterName = ChronoAuditInfo.Name;
}
