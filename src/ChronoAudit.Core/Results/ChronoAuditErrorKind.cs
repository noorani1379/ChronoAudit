namespace ChronoAudit;

/// <summary>The category of a <see cref="ChronoAuditError"/>; hosts map it to a transport status (the ASP.NET Core package maps it to HTTP).</summary>
public enum ChronoAuditErrorKind
{
    /// <summary>The input is invalid.</summary>
    Validation = 1,

    /// <summary>The caller is not authenticated.</summary>
    Unauthorized = 2,

    /// <summary>The caller is authenticated but not allowed.</summary>
    Forbidden = 3,

    /// <summary>The requested item does not exist or is not visible to the caller.</summary>
    NotFound = 4,

    /// <summary>The request conflicts with the current state.</summary>
    Conflict = 5,

    /// <summary>The request is well-formed but cannot be processed in the current state.</summary>
    Unprocessable = 6,

    /// <summary>A required dependency could not be reached or the evidence could not be stored.</summary>
    Unavailable = 7,
}
