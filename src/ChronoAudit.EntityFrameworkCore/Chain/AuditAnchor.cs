using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The head of one chain at a moment: row <see cref="ChainIndex"/> carried <see cref="PayloadHash"/>.</summary>
/// <param name="Module">The module (chain) name.</param>
/// <param name="ChainIndex">The chain index of the anchored row.</param>
/// <param name="PayloadHash">The hash of that row.</param>
/// <param name="AtUtc">When the anchor was written.</param>
/// <param name="PrevMac">The MAC of the previous line of the same file; null for the first line.</param>
/// <param name="Mac">The MAC of this line.</param>
/// <param name="KeyFingerprint">The fingerprint of the key that signed the line; null for a line written before key fingerprints existed.</param>
public sealed record AuditAnchor(string Module, long ChainIndex, string PayloadHash, DateTimeOffset AtUtc, string? PrevMac, string Mac, string? KeyFingerprint = null);

/// <summary>The outcome of comparing anchors with the database: how many were checked and the chain indexes that no longer match.</summary>
/// <param name="Checked">How many anchors were compared.</param>
/// <param name="Mismatches">The chain indexes whose row is missing or has another hash.</param>
public sealed record AnchorCheck(int Checked, IReadOnlyList<long> Mismatches)
{
    /// <summary>No anchors were supplied, so nothing was checked.</summary>
    public static AnchorCheck None { get; } = new(0, []);
}

/// <summary>What the anchor files hold: the lines whose MAC and link are intact, and how many lines were edited, cut or inserted.</summary>
/// <param name="Anchors">The trusted anchors.</param>
/// <param name="TamperedLines">How many lines failed the MAC or the link check.</param>
public sealed record AnchorRead(IReadOnlyList<AuditAnchor> Anchors, int TamperedLines);

/// <summary>
/// The external evidence that makes deleting the NEWEST rows detectable: once a day the head of every chain is appended to a file
/// outside the database. Each line carries an HMAC (the chain key named in the line) over its fields and the MAC of the previous line,
/// so editing, cutting or inserting a line is found when the file is read. Append-only; a failed write is logged and never crashes the
/// caller. Copy the directory off the server, otherwise whoever owns the server owns both the database and this file.
/// </summary>
public sealed class AuditAnchorLog
{
    private readonly object _gate = new();
    private readonly AuditKeyRing _keyRing;
    private readonly string _directory;
    private readonly ILogger<AuditAnchorLog> _logger;

    /// <summary>Creates the log.</summary>
    /// <param name="keyRing">The keys that sign and check the lines.</param>
    /// <param name="directory">The directory of the daily files.</param>
    /// <param name="logger">Where write failures are logged.</param>
    public AuditAnchorLog(AuditKeyRing keyRing, string directory, ILogger<AuditAnchorLog> logger)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);
        _keyRing = keyRing;
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Appends one anchor to the file of the day. Returns false when the file could not be written (the error is logged).</summary>
    /// <param name="module">The module (chain) name.</param>
    /// <param name="chainIndex">The chain index of the head.</param>
    /// <param name="payloadHash">The hash of the head.</param>
    /// <param name="atUtc">The time of the anchor.</param>
    /// <returns>True when the line was written.</returns>
    public bool TryAppend(string module, long chainIndex, string payloadHash, DateTimeOffset atUtc)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"anchors-{atUtc.UtcDateTime:yyyyMMdd}.jsonl");
                var prevMac = LastMac(path);
                var fingerprint = _keyRing.CurrentFingerprint;
                var mac = ComputeMac(_keyRing.Current, module, chainIndex, payloadHash, atUtc, prevMac, fingerprint);
                var line = JsonSerializer.Serialize(new Line(module, chainIndex, payloadHash, atUtc.ToString("O", CultureInfo.InvariantCulture), prevMac, mac, fingerprint));

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Audit anchor for {Module} at chain index {ChainIndex} could not be written to {Directory}.", module, chainIndex, _directory);
            return false;
        }
    }

    /// <summary>Reads every file in the directory. A line is trusted only when its MAC matches (with the key named in the line) and it links to the line before it.</summary>
    /// <returns>The trusted anchors and the number of tampered lines.</returns>
    public AnchorRead ReadAll()
    {
        var anchors = new List<AuditAnchor>();
        var tampered = 0;
        if (!Directory.Exists(_directory))
        {
            return new AnchorRead(anchors, 0);
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "anchors-*.jsonl").Order(StringComparer.Ordinal))
        {
            string? previousMac = null;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var line = TryParse(raw);
                if (line is null || !DateTimeOffset.TryParse(line.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                {
                    tampered++;
                    previousMac = null;
                    continue;
                }

                // A line without a fingerprint was written with the current key.
                var key = line.KeyFingerprint is null ? _keyRing.Current : (_keyRing.TryGet(line.KeyFingerprint, out var found) ? found : null);
                var intact = key is not null
                             && string.Equals(line.PrevMac, previousMac, StringComparison.Ordinal)
                             && string.Equals(line.Mac, ComputeMac(key, line.Module, line.ChainIndex, line.PayloadHash, at, line.PrevMac, line.KeyFingerprint), StringComparison.Ordinal);
                if (intact)
                {
                    anchors.Add(new AuditAnchor(line.Module, line.ChainIndex, line.PayloadHash, at, line.PrevMac, line.Mac, line.KeyFingerprint));
                }
                else
                {
                    tampered++;
                }

                previousMac = line.Mac;
            }
        }

        return new AnchorRead(anchors, tampered);
    }

    private static string ComputeMac(byte[] key, string module, long chainIndex, string payloadHash, DateTimeOffset atUtc, string? prevMac, string? fingerprint)
    {
        using var hasher = new FieldChainHasher(key);
        var fields = new List<string?>
        {
            module,
            chainIndex.ToString(CultureInfo.InvariantCulture),
            payloadHash,
            atUtc.ToString("O", CultureInfo.InvariantCulture),
        };
        if (fingerprint is not null)
        {
            fields.Add(fingerprint);
        }

        return hasher.Compute(prevMac, fields);
    }

    private static Line? TryParse(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<Line>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? LastMac(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var last = File.ReadLines(path, Encoding.UTF8).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return last is null ? null : TryParse(last)?.Mac;
    }

    private sealed record Line(string Module, long ChainIndex, string PayloadHash, string AtUtc, string? PrevMac, string Mac, string? KeyFingerprint = null);
}

/// <summary>The settings of the anchor worker (section <c>ChronoAudit:Anchors</c>).</summary>
public sealed class AnchorOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Anchors";

    /// <summary>The directory of the anchor files; a relative path is taken against <see cref="AppContext.BaseDirectory"/>.</summary>
    public string Directory { get; set; } = "App_Data/chronoaudit-anchors";

    /// <summary>Hours between two anchoring rounds (at least 1).</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Seconds to wait after start before the first round, so the first hashing pass finishes (at least 0).</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>How many of the newest rows of each chain are re-verified in a round (at least 1).</summary>
    public int VerifyWindowRows { get; set; } = 10_000;

    /// <summary>The directory as an absolute path.</summary>
    public string ResolvedDirectory => Path.IsPathRooted(Directory) ? Directory : Path.Combine(AppContext.BaseDirectory, Directory);

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            throw new InvalidOperationException("ChronoAudit:Anchors:Directory must not be empty.");
        }

        if (IntervalHours < 1)
        {
            throw new InvalidOperationException("ChronoAudit:Anchors:IntervalHours must be at least 1.");
        }

        if (StartupDelaySeconds < 0)
        {
            throw new InvalidOperationException("ChronoAudit:Anchors:StartupDelaySeconds must be at least 0.");
        }

        if (VerifyWindowRows < 1)
        {
            throw new InvalidOperationException("ChronoAudit:Anchors:VerifyWindowRows must be at least 1.");
        }
    }
}
