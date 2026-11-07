namespace ChronoAudit;

/// <summary>A predictable failure: a stable English <c>Area.Reason</c> code, a message and a kind.</summary>
/// <param name="Code">The stable machine-readable code, for example <c>Export.NotFound</c>.</param>
/// <param name="Message">A short English description.</param>
/// <param name="Kind">The category of the failure.</param>
public sealed record ChronoAuditError(string Code, string Message, ChronoAuditErrorKind Kind)
{
    /// <summary>Creates a <see cref="ChronoAuditErrorKind.Validation"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError Validation(string code, string message) => new(code, message, ChronoAuditErrorKind.Validation);

    /// <summary>Creates an <see cref="ChronoAuditErrorKind.Unauthorized"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError Unauthorized(string code, string message) => new(code, message, ChronoAuditErrorKind.Unauthorized);

    /// <summary>Creates a <see cref="ChronoAuditErrorKind.Forbidden"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError Forbidden(string code, string message) => new(code, message, ChronoAuditErrorKind.Forbidden);

    /// <summary>Creates a <see cref="ChronoAuditErrorKind.NotFound"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError NotFound(string code, string message) => new(code, message, ChronoAuditErrorKind.NotFound);

    /// <summary>Creates a <see cref="ChronoAuditErrorKind.Conflict"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError Conflict(string code, string message) => new(code, message, ChronoAuditErrorKind.Conflict);

    /// <summary>Creates an <see cref="ChronoAuditErrorKind.Unprocessable"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError Unprocessable(string code, string message) => new(code, message, ChronoAuditErrorKind.Unprocessable);

    /// <summary>Creates an <see cref="ChronoAuditErrorKind.Unavailable"/> error.</summary>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The error.</returns>
    public static ChronoAuditError Unavailable(string code, string message) => new(code, message, ChronoAuditErrorKind.Unavailable);
}
