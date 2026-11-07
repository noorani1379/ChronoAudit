using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChronoAudit;

/// <summary>
/// Hides secrets in what the activity log stores: the query string and the request body. Passwords, one-time codes, tokens and keys
/// become <see cref="MaskingOptions.Mask"/> wherever they are, at any depth, whatever the letter case, in JSON, in a form body and in
/// a body that cannot be parsed (a cut or malformed body is masked by pattern, because a secret must never be stored just because the
/// body was broken). Personal data is not hidden here: it is kept whole as evidence and hidden only when shown. Pure, no I/O.
/// </summary>
/// <remarks>
/// The field <c>code</c> is a secret only under the configured secret paths (authentication, one-time codes, sessions); on every
/// other endpoint it is a business code and masking it would destroy the evidence.
/// </remarks>
public sealed class SensitiveDataMasker
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // "key" : value  where value is a (possibly unterminated) string or a bare token. Used when the text is not whole, valid JSON.
    private static readonly Regex JsonPair = new(
        "(?<key>\"(?:[^\"\\\\]|\\\\.)*\")(?<sep>\\s*:\\s*)(?<value>\"(?:[^\"\\\\]|\\\\.)*\"?|[^,}\\]\\s\"]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HashSet<string> _exactNames;
    private readonly string[] _contains;
    private readonly string[] _suffixes;
    private readonly string[] _codeIsSecretUnder;

    /// <summary>Creates a masker from a snapshot of <paramref name="options"/>.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public SensitiveDataMasker(MaskingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _exactNames = new HashSet<string>(options.SecretNames.Select(Normalize), StringComparer.Ordinal);
        _contains = options.SecretNameContains.Select(Normalize).Where(s => s.Length > 0).ToArray();
        _suffixes = options.SecretNameSuffixes.Select(Normalize).Where(s => s.Length > 0).ToArray();
        _codeIsSecretUnder = options.CodeIsSecretPathFragments.Where(s => !string.IsNullOrEmpty(s)).ToArray();
    }

    /// <summary>True when <paramref name="path"/> is an endpoint where the field <c>code</c> is a one-time secret.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>True when <c>code</c> is a secret under this path.</returns>
    public bool CodeIsSecret(string? path)
        => path is not null && _codeIsSecretUnder.Any(fragment => path.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a field called <paramref name="name"/> holds a secret.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="codeIsSecret">Whether the field <c>code</c> counts as a secret for this request.</param>
    /// <returns>True when the value must be masked.</returns>
    public bool IsSecretName(string? name, bool codeIsSecret)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var key = Normalize(name);
        return _exactNames.Contains(key)
               || _contains.Any(fragment => key.Contains(fragment, StringComparison.Ordinal))
               || _suffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal))
               || (codeIsSecret && key == "code");
    }

    /// <summary>The query string (with or without the leading <c>?</c>) with the value of every secret key replaced.</summary>
    /// <param name="query">The query string.</param>
    /// <param name="codeIsSecret">Whether <c>code</c> counts as a secret for this request.</param>
    /// <returns>The masked query string, or the input when it is null or empty.</returns>
    public string? MaskQueryString(string? query, bool codeIsSecret)
        => MaskPairs(query, codeIsSecret, keepLeadingQuestionMark: true);

    /// <summary>A form-urlencoded body with the value of every secret key replaced.</summary>
    /// <param name="body">The body.</param>
    /// <param name="codeIsSecret">Whether <c>code</c> counts as a secret for this request.</param>
    /// <returns>The masked body.</returns>
    public string MaskForm(string body, bool codeIsSecret)
        => MaskPairs(body, codeIsSecret, keepLeadingQuestionMark: false) ?? string.Empty;

    /// <summary>
    /// JSON text with the value of every secret property replaced, at any depth. Valid, whole JSON is rewritten structurally (compact,
    /// non-ASCII text kept readable); anything else, including a body cut at the size limit, is masked by pattern.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="codeIsSecret">Whether <c>code</c> counts as a secret for this request.</param>
    /// <returns>The masked text.</returns>
    public string MaskJson(string json, bool codeIsSecret)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                Write(writer, document.RootElement, codeIsSecret);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return MaskText(json, codeIsSecret);
        }
    }

    /// <summary>The pattern-based masking for text that is not whole JSON: every <c>"key": value</c> pair whose key is a secret.</summary>
    /// <param name="text">The text.</param>
    /// <param name="codeIsSecret">Whether <c>code</c> counts as a secret for this request.</param>
    /// <returns>The masked text.</returns>
    public string MaskText(string text, bool codeIsSecret)
    {
        try
        {
            return JsonPair.Replace(text, match =>
                IsSecretName(Unquote(match.Groups["key"].Value), codeIsSecret)
                    ? match.Groups["key"].Value + match.Groups["sep"].Value + "\"" + MaskingOptions.Mask + "\""
                    : match.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological text: store nothing of it rather than risk a secret.
            return "\"" + MaskingOptions.Mask + "\"";
        }
    }

    private static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch is not ('_' or '-' or ' '))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private string? MaskPairs(string? text, bool codeIsSecret, bool keepLeadingQuestionMark)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var prefix = keepLeadingQuestionMark && text[0] == '?' ? "?" : string.Empty;
        var pairs = text[prefix.Length..].Split('&');

        for (var i = 0; i < pairs.Length; i++)
        {
            var equals = pairs[i].IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            var key = Decode(pairs[i][..equals]);
            if (IsSecretName(key, codeIsSecret))
            {
                pairs[i] = pairs[i][..equals] + "=" + MaskingOptions.Mask;
            }
        }

        return prefix + string.Join('&', pairs);
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string Unquote(string quoted)
        => quoted.Length >= 2 ? quoted[1..^1] : quoted;

    private void Write(Utf8JsonWriter writer, JsonElement element, bool codeIsSecret)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSecretName(property.Name, codeIsSecret))
                    {
                        writer.WriteStringValue(MaskingOptions.Mask);
                    }
                    else
                    {
                        Write(writer, property.Value, codeIsSecret);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item, codeIsSecret);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
