namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// The knobs of the audit pipeline. Every default is neutral and in English; a host with stored history in another language overrides them
/// (the values are written into audit rows and read models, so change them before the first row exists, never afterwards).
/// </summary>
public sealed class AuditTrailOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Audit";

    private string _delegatedDisplayNameFormat = "{0} (on behalf)";

    /// <summary>The options a context uses when it does not override <see cref="AuditableDbContext.AuditOptions"/>.</summary>
    public static AuditTrailOptions Default { get; } = new();

    /// <summary>The actor id recorded when no user is signed in (seed, background job). Default <c>System</c>.</summary>
    public string SystemUserId { get; set; } = "System";

    /// <summary>The display name recorded when no user is signed in. Default <c>System</c>.</summary>
    public string SystemDisplayName { get; set; } = "System";

    /// <summary>
    /// The format of the display name when the action is delegated; <c>{0}</c> is the name. Default <c>{0} (on behalf)</c>.
    /// It is checked when it is set: it must be a valid composite format that uses only <c>{0}</c> and contains it, because it runs inside every
    /// save of a delegated actor and a bad format would fail all of them.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty, is not a valid format, or does not contain <c>{0}</c>.</exception>
    public string DelegatedDisplayNameFormat
    {
        get => _delegatedDisplayNameFormat;
        set => _delegatedDisplayNameFormat = ValidateFormat(value);
    }

    /// <summary>The hash worker logs a warning when a module has more than this many unhashed audit rows. Default 1000.</summary>
    public int BacklogWarnRows { get; set; } = 1000;

    private static string ValidateFormat(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string marker = "0001name0001";
        try
        {
            if (!string.Format(System.Globalization.CultureInfo.InvariantCulture, value, marker).Contains(marker, StringComparison.Ordinal))
            {
                throw new ArgumentException("The format must contain {0}, where the display name goes.", nameof(value));
            }
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The format is not a valid composite format that uses only {0}.", nameof(value), ex);
        }

        return value;
    }
}
