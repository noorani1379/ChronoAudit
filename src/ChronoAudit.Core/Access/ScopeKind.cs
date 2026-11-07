namespace ChronoAudit;

/// <summary>How wide the data of one request is. The host decides it from the caller's permissions, never from a query parameter.</summary>
public enum ScopeKind
{
    /// <summary>Only the caller's own items.</summary>
    Own = 1,

    /// <summary>The caller's own items and the items of the caller's group.</summary>
    Group = 2,

    /// <summary>Everything.</summary>
    All = 3,
}
