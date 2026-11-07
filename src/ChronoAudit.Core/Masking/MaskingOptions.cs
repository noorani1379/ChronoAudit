namespace ChronoAudit;

/// <summary>
/// Which field names hold secrets and how much of a body is kept. Names are compared after the same normalization the reference
/// used (lower-case, <c>_</c> <c>-</c> and spaces removed), so <c>refresh-token</c> and <c>RefreshToken</c> are the same name.
/// The masker takes a snapshot of these lists when it is created.
/// </summary>
public sealed class MaskingOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Masking";

    /// <summary>The marker that replaces a secret value.</summary>
    public const string Mask = "***MASKED***";

    /// <summary>Names that are secrets exactly (after normalization).</summary>
    public ISet<string> SecretNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "otp", "otpcode", "captchacode", "apikey", "twofactorcode", "pin", "passcode", "cvv2",
        "authorization", "cookie", "setcookie", "cvv",
    };

    /// <summary>Names that are secrets when they contain one of these fragments.</summary>
    public IList<string> SecretNameContains { get; } = new List<string> { "password" };

    /// <summary>Names that are secrets when they end with one of these fragments.</summary>
    public IList<string> SecretNameSuffixes { get; } = new List<string> { "token", "secret" };

    /// <summary>Request paths under which the field <c>code</c> is a one-time secret (elsewhere it is a business value).</summary>
    public IList<string> CodeIsSecretPathFragments { get; } = new List<string> { "/auth/", "/otp/", "/sessions/" };

    /// <summary>The largest body (in bytes) that is stored; a longer body is cut and marked as truncated.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;
}
