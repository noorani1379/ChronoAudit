using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The analytics subjects over ChronoAudit's own data: the audit rows of each module and the API activity log.</summary>
internal static class ChronoAuditAnalyticsSubjects
{
    /// <summary>The subject key of the API activity log.</summary>
    public const string ActivityKey = "activity";

    /// <summary>The subject key of the timeline events.</summary>
    public const string TimelineKey = "timeline";

    public static string AuditKey(string module) => "audit:" + module;

    /// <summary>Adds the subjects whose data exists: one per audited module, and the activity log when it was added.</summary>
    public static void AddTo(ChronoAuditRegistry registry)
    {
        foreach (var (contextType, module) in registry.Modules)
        {
            if (registry.AnalyticsSubjects.All(s => s.Key != AuditKey(module)))
            {
                registry.AnalyticsSubjects.Add(Audit(module, contextType));
            }
        }

        if (registry.TimelineAdded && registry.AnalyticsSubjects.All(s => s.Key != TimelineKey))
        {
            registry.AnalyticsSubjects.Add(Timeline());
        }

        if (registry.ActivityLogAdded && registry.AnalyticsSubjects.All(s => s.Key != ActivityKey))
        {
            registry.AnalyticsSubjects.Add(Activity());
        }
    }

    // Day, month, quarter and year numbers from the stored CalendarDate (year*10000 + month*100 + day), so the configured calendar applies.
    private static RegisteredAnalyticsSubject<AuditLogEntry> Audit(string module, Type contextType) => new RegisteredAnalyticsSubject<AuditLogEntry>(
        new AnalyticsSubject<AuditLogEntry>(
            AuditKey(module),
            "Audit rows of " + module,
            a => a.OccurredAtUtc,
            [
                new AnalyticsDimension<AuditLogEntry>("action", "Action", a => a.Action),
                new AnalyticsDimension<AuditLogEntry>("resourceType", "Table", a => a.ResourceType),
                new AnalyticsDimension<AuditLogEntry>("actor", "Actor", a => a.ActorUserId),
                AnalyticsDimension.Numeric<AuditLogEntry>("day", "Day", a => (long?)a.CalendarDate, TimeGrain.Day),
                AnalyticsDimension.Numeric<AuditLogEntry>("month", "Month", a => (long?)(a.CalendarDate / 100), TimeGrain.Month),
                AnalyticsDimension.Numeric<AuditLogEntry>("quarter", "Quarter", a => (long?)(((a.CalendarDate / 10000) * 10) + ((((a.CalendarDate / 100) % 100) - 1) / 3) + 1), TimeGrain.Quarter),
                AnalyticsDimension.Numeric<AuditLogEntry>("year", "Year", a => (long?)(a.CalendarDate / 10000), TimeGrain.Year),
            ],
            [
                AnalyticsMeasure<AuditLogEntry>.Count("count", "Rows"),
                AnalyticsMeasure<AuditLogEntry>.CountDistinct("distinctActors", "Distinct actors", a => a.ActorUserId),
                AnalyticsMeasure<AuditLogEntry>.CountDistinct("distinctResources", "Distinct resources", a => a.ResourceType + "|" + a.ResourceId),
            ],
            [
                FilterField.Of<AuditLogEntry, string>("action", "Action", a => a.Action, FilterOp.Eq, FilterOp.In),
                FilterField.Of<AuditLogEntry, string>("resourceType", "Table", a => a.ResourceType, FilterOp.Eq, FilterOp.In),
                FilterField.Of<AuditLogEntry, string>("actor", "Actor", a => a.ActorUserId, FilterOp.Eq, FilterOp.In),
                FilterField.Of<AuditLogEntry, string>("resourceId", "Record", a => a.ResourceId, FilterOp.Eq),
            ])
        {
            RequiresAllScope = true,
        },
        (services, _) => ((AuditableDbContext)services.GetRequiredService(contextType)).AuditLogEntries);

    // Business events; the day, month, quarter and year come from the stored CalendarDate, like the audit rows. The scope is applied inside the source.
    private static RegisteredAnalyticsSubject<TimelineEvent> Timeline() => new RegisteredAnalyticsSubject<TimelineEvent>(
        new AnalyticsSubject<TimelineEvent>(
            TimelineKey,
            "Timeline events",
            e => e.OccurredAtUtc,
            [
                new AnalyticsDimension<TimelineEvent>("eventType", "Event type", e => e.EventType),
                new AnalyticsDimension<TimelineEvent>("subjectType", "Subject type", e => e.SubjectType),
                new AnalyticsDimension<TimelineEvent>("actorRole", "Actor role", e => e.ActorRole),
                AnalyticsDimension.Numeric<TimelineEvent>("day", "Day", e => (long?)e.CalendarDate, TimeGrain.Day),
                AnalyticsDimension.Numeric<TimelineEvent>("month", "Month", e => (long?)(e.CalendarDate / 100), TimeGrain.Month),
                AnalyticsDimension.Numeric<TimelineEvent>("quarter", "Quarter", e => (long?)(((e.CalendarDate / 10000) * 10) + ((((e.CalendarDate / 100) % 100) - 1) / 3) + 1), TimeGrain.Quarter),
                AnalyticsDimension.Numeric<TimelineEvent>("year", "Year", e => (long?)(e.CalendarDate / 10000), TimeGrain.Year),
            ],
            [
                AnalyticsMeasure<TimelineEvent>.Count("count", "Events"),
                AnalyticsMeasure<TimelineEvent>.CountDistinct("distinctSubjects", "Distinct subjects", e => e.SubjectType + "|" + e.SubjectId),
                AnalyticsMeasure<TimelineEvent>.CountDistinct("distinctActors", "Distinct actors", e => e.ActorUserId),
            ],
            [
                FilterField.Of<TimelineEvent, string>("eventType", "Event type", e => e.EventType, FilterOp.Eq, FilterOp.In, FilterOp.Prefix),
                FilterField.Of<TimelineEvent, string>("subjectType", "Subject type", e => e.SubjectType, FilterOp.Eq, FilterOp.In),
                FilterField.Of<TimelineEvent, string?>("actor", "Actor", e => e.ActorUserId, FilterOp.Eq),
                FilterField.Of<TimelineEvent, string?>("groupKey", "Group key", e => e.GroupKey, FilterOp.Eq),
            ])
        {
            RequiresAllScope = false,
        },
        (services, scope) =>
        {
            var registration = services.GetRequiredService<ChronoAuditStoreRegistration>();
            var db = (DbContext)services.GetRequiredService(registration.ContextType);
            return TimelineScope.Visible(db, db.Set<TimelineEvent>().AsQueryable(), scope);
        });

    private static RegisteredAnalyticsSubject<ApiRequestLog> Activity() => new RegisteredAnalyticsSubject<ApiRequestLog>(
        new AnalyticsSubject<ApiRequestLog>(
            ActivityKey,
            "API activity",
            l => l.OccurredAtUtc,
            [
                AnalyticsDimension.Numeric<ApiRequestLog>("day", "Day", l => (l.CalendarYear * 10000L) + (l.CalendarMonth * 100L) + l.CalendarDay, TimeGrain.Day),
                AnalyticsDimension.Numeric<ApiRequestLog>("month", "Month", l => (l.CalendarYear * 100L) + l.CalendarMonth, TimeGrain.Month),
                AnalyticsDimension.Numeric<ApiRequestLog>("quarter", "Quarter", l => (l.CalendarYear * 10L) + l.CalendarQuarter, TimeGrain.Quarter),
                AnalyticsDimension.Numeric<ApiRequestLog>("year", "Year", l => (long?)l.CalendarYear, TimeGrain.Year),
                new AnalyticsDimension<ApiRequestLog>("endpoint", "Endpoint", l => l.EndpointKey),
                new AnalyticsDimension<ApiRequestLog>("method", "Method", l => l.HttpMethod),
                AnalyticsDimension.Numeric<ApiRequestLog>("statusClass", "Status class", l => (long?)((l.StatusCode / 100) * 100)),
                new AnalyticsDimension<ApiRequestLog>("user", "User", l => l.UserId),
                new AnalyticsDimension<ApiRequestLog>("errorCode", "Error code", l => l.ErrorCode),
            ],
            [
                AnalyticsMeasure<ApiRequestLog>.Count("count", "Calls"),
                AnalyticsMeasure<ApiRequestLog>.CountDistinct("distinctUsers", "Distinct users", l => l.UserId),
                AnalyticsMeasure<ApiRequestLog>.Of("avgDurationMs", "Average duration (ms)", Aggregation.Avg, l => (decimal?)l.DurationMs),
                AnalyticsMeasure<ApiRequestLog>.Of("maxDurationMs", "Longest duration (ms)", Aggregation.Max, l => (decimal?)l.DurationMs),
                AnalyticsMeasure<ApiRequestLog>.Of("errors", "Server errors", Aggregation.Sum, l => l.StatusCode >= 500 ? 1m : 0m),
            ],
            [
                FilterField.Of<ApiRequestLog, string?>("endpoint", "Endpoint", l => l.EndpointKey, FilterOp.Eq, FilterOp.In, FilterOp.Prefix),
                FilterField.Of<ApiRequestLog, short>("status", "Status", l => l.StatusCode, FilterOp.Range),
                FilterField.Of<ApiRequestLog, string?>("user", "User", l => l.UserId, FilterOp.Eq, FilterOp.In),
                FilterField.Of<ApiRequestLog, string>("method", "Method", l => l.HttpMethod, FilterOp.Eq, FilterOp.In),
                FilterField.Of<ApiRequestLog, string?>("errorCode", "Error code", l => l.ErrorCode, FilterOp.Eq, FilterOp.IsNull),
            ])
        {
            RequiresAllScope = false,
        },
        (services, scope) =>
        {
            var registration = services.GetRequiredService<ChronoAuditStoreRegistration>();
            var rows = ((DbContext)services.GetRequiredService(registration.ContextType)).Set<ApiRequestLog>().AsQueryable();

            // Own sees its calls; Group is not supported for the activity log and is treated as Own; All sees everything.
            if (scope.Kind == ScopeKind.All)
            {
                return rows;
            }

            var user = scope.UserId;
            return rows.Where(l => l.UserId == user && user != null);
        });
}
