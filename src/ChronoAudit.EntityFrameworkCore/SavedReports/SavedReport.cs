namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// A report definition a user saved to run again: the analytics query as JSON, owned by one user and optionally shared with one group.
/// The definition is checked against the current catalog every time it runs; a key that was removed since makes it outdated, never
/// silently different. <see cref="ConcurrencyStamp"/> is a concurrency token: two people editing the same report cannot overwrite each other.
/// </summary>
public sealed class SavedReport
{
    /// <summary>The longest title, in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The longest stored definition, in characters.</summary>
    public const int MaxDefinitionLength = 16_000;

    /// <summary>The longest id of an owner, a group or a subject, in characters.</summary>
    public const int MaxIdLength = 64;

    /// <summary>The version of the stored definition format.</summary>
    public const int CurrentDefinitionVersion = 1;

    private SavedReport()
    {
    }

    private SavedReport(string ownerUserId, string title, string subject, string definitionJson, string? sharedWithGroupId, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        OwnerUserId = ownerUserId;
        Title = title;
        Subject = subject;
        DefinitionJson = definitionJson;
        DefinitionVersion = CurrentDefinitionVersion;
        SharedWithGroupId = sharedWithGroupId;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>The report id.</summary>
    public Guid Id { get; private set; }

    /// <summary>The user who created the report; only this user changes or deletes it.</summary>
    public string OwnerUserId { get; private set; } = string.Empty;

    /// <summary>The title (trimmed).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The analytics subject key, kept as a column so a list can show it without reading the JSON.</summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>The analytics query as JSON.</summary>
    public string DefinitionJson { get; private set; } = string.Empty;

    /// <summary>The version of the format of <see cref="DefinitionJson"/>.</summary>
    public int DefinitionVersion { get; private set; }

    /// <summary>The group the report is shared with; null means private to its owner.</summary>
    public string? SharedWithGroupId { get; private set; }

    /// <summary>True when the report is shared with a group.</summary>
    public bool IsShared => SharedWithGroupId is not null;

    /// <summary>When the report was created (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When the report was last changed (UTC).</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Renewed by every change; a save whose stamp moved meanwhile is a conflict.</summary>
    public Guid ConcurrencyStamp { get; private set; }

    /// <summary>Creates a report.</summary>
    /// <param name="ownerUserId">The owner.</param>
    /// <param name="title">The title.</param>
    /// <param name="subject">The analytics subject key.</param>
    /// <param name="definitionJson">The query as JSON.</param>
    /// <param name="sharedWithGroupId">The group to share with, or null.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The report, or <c>SavedReport.TitleInvalid</c> / <c>SavedReport.DefinitionInvalid</c>.</returns>
    public static ChronoAuditResult<SavedReport> Create(string ownerUserId, string title, string subject, string definitionJson, string? sharedWithGroupId, DateTimeOffset now)
    {
        var check = Validate(ownerUserId, title, subject, definitionJson, sharedWithGroupId);
        return check.IsFailure
            ? ChronoAuditResult.Failure<SavedReport>(check.Error)
            : ChronoAuditResult.Success(new SavedReport(ownerUserId, title.Trim(), subject, definitionJson, sharedWithGroupId, now));
    }

    /// <summary>Replaces the title, the definition and the sharing, and renews the concurrency stamp.</summary>
    /// <param name="title">The title.</param>
    /// <param name="subject">The analytics subject key.</param>
    /// <param name="definitionJson">The query as JSON.</param>
    /// <param name="sharedWithGroupId">The group to share with, or null.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>Success, or the same errors as <see cref="Create"/>.</returns>
    public ChronoAuditResult Change(string title, string subject, string definitionJson, string? sharedWithGroupId, DateTimeOffset now)
    {
        var check = Validate(OwnerUserId, title, subject, definitionJson, sharedWithGroupId);
        if (check.IsFailure)
        {
            return check;
        }

        Title = title.Trim();
        Subject = subject;
        DefinitionJson = definitionJson;
        DefinitionVersion = CurrentDefinitionVersion;
        SharedWithGroupId = sharedWithGroupId;
        UpdatedAtUtc = now;
        ConcurrencyStamp = Guid.NewGuid();
        return ChronoAuditResult.Success();
    }

    private static ChronoAuditResult Validate(string ownerUserId, string title, string subject, string definitionJson, string? sharedWithGroupId)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Validation("SavedReport.TitleInvalid", $"The report title is required and may have at most {MaxTitleLength} characters."));
        }

        if (string.IsNullOrWhiteSpace(definitionJson) || definitionJson.Length > MaxDefinitionLength
            || string.IsNullOrWhiteSpace(subject) || subject.Length > MaxIdLength)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Validation("SavedReport.DefinitionInvalid", "The report definition is empty or too large."));
        }

        if (string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId.Length > MaxIdLength
            || (sharedWithGroupId is not null && (string.IsNullOrWhiteSpace(sharedWithGroupId) || sharedWithGroupId.Length > MaxIdLength)))
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Validation("ChronoAudit.Validation", $"The owner and the group id are required when given and may have at most {MaxIdLength} characters."));
        }

        return ChronoAuditResult.Success();
    }
}
