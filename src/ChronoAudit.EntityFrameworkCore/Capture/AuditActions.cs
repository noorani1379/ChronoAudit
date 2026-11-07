namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The <c>Action</c> values the library itself writes. A host adds its own constants for rows it appends by hand.</summary>
public static class AuditActions
{
    /// <summary>A tracked entity was inserted.</summary>
    public const string AutoAdded = "Auto_Added";

    /// <summary>A tracked entity was updated.</summary>
    public const string AutoModified = "Auto_Modified";

    /// <summary>A tracked entity was soft deleted (<c>IsDeleted</c> changed from false to true).</summary>
    public const string AutoSoftDeleted = "Auto_SoftDeleted";

    /// <summary>A tracked entity was physically deleted.</summary>
    public const string AutoDeleted = "Auto_Deleted";

    /// <summary>The HMAC key of a chain was rotated.</summary>
    public const string KeyRotated = "ChronoAudit.KeyRotated";

    /// <summary>A background export started.</summary>
    public const string ExportStarted = "ChronoAudit.ExportStarted";

    /// <summary>A background export finished.</summary>
    public const string ExportFinished = "ChronoAudit.ExportFinished";

    /// <summary>The file of an export was downloaded.</summary>
    public const string ExportDownloaded = "ChronoAudit.ExportDownloaded";

    /// <summary>An export was cancelled.</summary>
    public const string ExportCancelled = "ChronoAudit.ExportCancelled";

    /// <summary>A hash chain was verified.</summary>
    public const string ChainVerified = "ChronoAudit.ChainVerified";

    /// <summary>The stored body of an API request was viewed.</summary>
    public const string ActivityBodyViewed = "ChronoAudit.ActivityBodyViewed";

    /// <summary>The activity log was exported as a CSV file.</summary>
    public const string ActivityExported = "ChronoAudit.ActivityExported";

    /// <summary>An extract of all changes finished (or was cut): the row count, the hash and whether it completed.</summary>
    public const string ExtractFinished = "ChronoAudit.ExtractFinished";

    /// <summary>An extract of all changes was run.</summary>
    public const string ExtractRun = "ChronoAudit.ExtractRun";

    /// <summary>Masked personal data was revealed to a caller.</summary>
    public const string PersonalDataRevealed = "ChronoAudit.PersonalDataRevealed";

    /// <summary>An evidence bundle was issued.</summary>
    public const string EvidenceIssued = "ChronoAudit.EvidenceIssued";
}
