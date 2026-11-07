using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ChronoAudit;

/// <summary>The masked, size-limited form of a request body, ready to store.</summary>
/// <param name="Text">The masked text of a text body, or null.</param>
/// <param name="FileMetadata">The JSON description of a multipart request (fields masked, files described), or null.</param>
/// <param name="IsTruncated">True when the body was longer than what is stored.</param>
public sealed record MaskedBody(string? Text, string? FileMetadata, bool IsTruncated);

/// <summary>
/// Turns what the endpoint read of a request body into what the activity log may store: text only (JSON, form, plain text or XML),
/// at most <see cref="MaskingOptions.MaxBodyBytes"/> bytes, secrets masked. Anything else (a binary upload) keeps its content type but
/// no content. Pure, no I/O.
/// </summary>
public sealed class BodyMasker
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SensitiveDataMasker _masker;
    private readonly int _maxBodyBytes;

    /// <summary>Creates a body masker.</summary>
    /// <param name="masker">The field masker.</param>
    /// <param name="options">The options (the size limit is read here).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public BodyMasker(SensitiveDataMasker masker, MaskingOptions options)
    {
        ArgumentNullException.ThrowIfNull(masker);
        ArgumentNullException.ThrowIfNull(options);
        _masker = masker;
        _maxBodyBytes = options.MaxBodyBytes;
    }

    /// <summary>True when the content of a body of this type is worth capturing as text.</summary>
    /// <param name="contentType">The <c>Content-Type</c> header value.</param>
    /// <returns>True for JSON, form, XML and <c>text/*</c> bodies.</returns>
    public static bool IsCapturable(string? contentType)
    {
        var type = MediaType(contentType);
        return type is "application/json" or "application/x-www-form-urlencoded" or "application/xml"
               || type.StartsWith("text/", StringComparison.Ordinal)
               || type.EndsWith("+json", StringComparison.Ordinal)
               || type.EndsWith("+xml", StringComparison.Ordinal);
    }

    /// <summary>True when the body is a multipart request.</summary>
    /// <param name="contentType">The <c>Content-Type</c> header value.</param>
    /// <returns>True for <c>multipart/*</c>.</returns>
    public static bool IsMultipart(string? contentType)
        => MediaType(contentType).StartsWith("multipart/", StringComparison.Ordinal);

    /// <summary>
    /// Masks the captured bytes (the first part of the body the endpoint read). <paramref name="isTruncated"/> says the body was longer
    /// than what was captured; a cut body is masked by pattern, so a secret in it is hidden even when the JSON is broken.
    /// </summary>
    /// <param name="captured">The captured bytes.</param>
    /// <param name="isTruncated">True when the body was longer than the captured part.</param>
    /// <param name="contentType">The <c>Content-Type</c> header value.</param>
    /// <param name="path">The request path (decides whether <c>code</c> is a secret).</param>
    /// <returns>The masked body.</returns>
    public MaskedBody MaskText(ReadOnlySpan<byte> captured, bool isTruncated, string? contentType, string? path)
    {
        var codeIsSecret = _masker.CodeIsSecret(path);
        var text = Encoding.UTF8.GetString(captured);
        var type = MediaType(contentType);

        string masked;
        if (type == "application/x-www-form-urlencoded")
        {
            masked = _masker.MaskForm(text, codeIsSecret);
        }
        else if (type.EndsWith("json", StringComparison.Ordinal) || LooksLikeJson(text))
        {
            masked = isTruncated ? _masker.MaskText(text, codeIsSecret) : _masker.MaskJson(text, codeIsSecret);
        }
        else
        {
            // XML and plain text: a "key": value pair can still occur inside; the pattern hides it.
            masked = _masker.MaskText(text, codeIsSecret);
        }

        return new MaskedBody(masked, null, isTruncated);
    }

    /// <summary>
    /// The multipart request as JSON: every non-file field with secrets masked, and for each file its field name, file name, size and
    /// content type (the content is never read or stored).
    /// </summary>
    /// <param name="fields">The non-file fields (a name may repeat).</param>
    /// <param name="files">The files.</param>
    /// <param name="isTruncated">True when the host already cut the field list.</param>
    /// <param name="path">The request path (decides whether <c>code</c> is a secret).</param>
    /// <returns>The description, with the JSON in <see cref="MaskedBody.FileMetadata"/>.</returns>
    public MaskedBody DescribeForm(
        IEnumerable<KeyValuePair<string, string>> fields,
        IEnumerable<(string Name, string FileName, string? ContentType, long Length)> files,
        bool isTruncated,
        string? path)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(files);

        var codeIsSecret = _masker.CodeIsSecret(path);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("fields");
            foreach (var group in fields.GroupBy(f => f.Key, StringComparer.Ordinal))
            {
                var secret = _masker.IsSecretName(group.Key, codeIsSecret);
                writer.WriteStartArray(group.Key);
                foreach (var field in group)
                {
                    writer.WriteStringValue(secret ? MaskingOptions.Mask : field.Value);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteStartArray("files");
            foreach (var (name, fileName, fileType, length) in files)
            {
                writer.WriteStartObject();
                writer.WriteString("field", name);
                writer.WriteString("fileName", fileName);
                writer.WriteNumber("size", length);
                writer.WriteString("contentType", fileType);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        var tooLong = stream.Length > _maxBodyBytes;
        if (tooLong)
        {
            // Up to four bytes per character, so this many characters always fit in the byte limit.
            var keep = _maxBodyBytes / 4;
            if (keep > 0 && keep < json.Length && char.IsHighSurrogate(json[keep - 1]))
            {
                keep--;
            }

            json = json[..keep];
        }

        return new MaskedBody(null, json, isTruncated || tooLong);
    }

    private static bool LooksLikeJson(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return ch is '{' or '[';
            }
        }

        return false;
    }

    private static string MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon < 0 ? contentType : contentType[..semicolon]).Trim().ToLowerInvariant();
    }
}
