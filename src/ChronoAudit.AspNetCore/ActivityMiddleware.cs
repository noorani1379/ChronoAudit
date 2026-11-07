using System.Diagnostics;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.AspNetCore;

/// <summary>
/// Reads a request body as the endpoint consumes it: every byte is hashed (the mutation intent keeps the SHA-256 of the whole body) and
/// a text body is also copied, up to a limit, for the masked log row. It never reads the body itself.
/// </summary>
internal sealed class CapturingReadStream(Stream inner, int limit) : Stream
{
    private readonly MemoryStream _copy = new();
    private readonly System.Security.Cryptography.IncrementalHash _sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    private bool _copyFailed;
    private bool _hashReleased;

    public long TotalRead { get; private set; }

    public ReadOnlySpan<byte> Captured => _copy.GetBuffer().AsSpan(0, (int)_copy.Length);

    public bool IsTruncated => TotalRead > _copy.Length;

    // The hash of every byte read so far (the empty-body hash when nothing was read).
    public string Sha256Hex() => _hashReleased ? string.Empty : Convert.ToHexString(_sha256.GetCurrentHash());

    public void ReleaseHash()
    {
        _hashReleased = true;
        _sha256.Dispose();
    }

    private void Capture(ReadOnlySpan<byte> data)
    {
        TotalRead += data.Length;
        if (!_hashReleased && data.Length > 0)
        {
            _sha256.AppendData(data);
        }

        if (_copyFailed || data.Length == 0)
        {
            return;
        }

        try
        {
            var room = limit - (int)_copy.Length;
            if (room > 0)
            {
                _copy.Write(data[..Math.Min(room, data.Length)]);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ObjectDisposedException or IOException)
        {
            _copyFailed = true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Capture(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Capture(buffer[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        Capture(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Capture(buffer.Span[..read]);
        return read;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // No Dispose override on purpose: the endpoint may close the body (a StreamReader does), and the copy must stay readable for the
    // log row afterwards. A MemoryStream holds no unmanaged resource.
}

/// <summary>
/// Records one row per API call in the activity log: who, what, how long, the status, the masked body. The row is written after the
/// call (with <c>CancellationToken.None</c>, because the caller may have gone away) and a failed write is logged, never thrown.
/// </summary>
internal sealed class ChronoAuditActivityMiddleware(
    RequestDelegate next,
    IApiRequestLogStore store,
    ActivityLogOptions activityOptions,
    ChronoAuditAspNetCoreOptions options,
    MaskingOptions maskingOptions,
    SensitiveDataMasker masker,
    BodyMasker bodyMasker,
    TimeProvider clock,
    ILogger<ChronoAuditActivityMiddleware> logger)
{
    internal const string CaptureItemKey = "ChronoAudit.ActivityLog.BodyCapture";

    internal bool IsLogged(PathString path)
        => !activityOptions.ExcludedPathPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLogged(context.Request.Path))
        {
            await next(context);
            return;
        }

        var occurredAt = clock.GetUtcNow();
        var watch = Stopwatch.StartNew();
        var failed = false;

        // Set before anything else runs: the mutation intent row and the result row of this call carry the same id.
        context.Items[ChronoAuditHttpItems.RequestId] = Guid.NewGuid();

        // Resolved now, so the correlation id (and its response header) exists before the response starts.
        _ = context.RequestServices.GetService<ICorrelationContext>();
        var capture = StartCapture(context);

        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            watch.Stop();
            await RecordAsync(context, occurredAt, watch.Elapsed, failed, capture);
            capture?.ReleaseHash();
        }
    }

    private async Task RecordAsync(HttpContext context, DateTimeOffset occurredAt, TimeSpan duration, bool failed, CapturingReadStream? capture)
    {
        try
        {
            var entry = Describe(context, occurredAt, duration, failed, capture);
            await store.WriteAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The API activity log row for {Method} {Path} could not be written.", context.Request.Method, context.Request.Path.Value);
        }
    }

    private ApiRequestLogEntry Describe(HttpContext context, DateTimeOffset occurredAt, TimeSpan duration, bool failed, CapturingReadStream? capture)
    {
        var request = context.Request;
        var user = context.User;
        var status = context.Response.StatusCode;

        if (failed && status < 400)
        {
            status = StatusCodes.Status500InternalServerError;
        }
        else if (context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            status = 499;
        }

        var path = request.Path.Value;
        var codeIsSecret = masker.CodeIsSecret(path);
        var body = DescribeBody(context, capture);

        return ApiRequestLogEntry.Create(
            requestId: context.Items.TryGetValue(ChronoAuditHttpItems.RequestId, out var id) && id is Guid requestId ? requestId : Guid.NewGuid(),
            occurredAtUtc: occurredAt,
            duration: duration,
            httpMethod: request.Method,
            path: path,
            queryString: masker.MaskQueryString(request.QueryString.Value, codeIsSecret),
            statusCode: status,
            userId: options.FirstClaim(user, options.UserIdClaimTypes),
            actAsUserId: user.FindFirst(options.ActAsUserIdClaimType)?.Value,
            sessionId: user.FindFirst(options.SessionIdClaimType)?.Value,
            correlationId: context.RequestServices.GetService<ICorrelationContext>() is { CorrelationId: var correlation } && correlation != Guid.Empty ? correlation : null,
            endpointKey: (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            errorCode: context.Items.TryGetValue(ChronoAuditHttpItems.ErrorCode, out var code) ? code as string : null,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            userAgent: request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null,
            hasBody: HasBodyBytes(request),
            body: body?.Text,
            bodyContentType: body is null ? null : request.ContentType,
            bodyIsTruncated: body?.IsTruncated ?? false,
            fileMetadata: body?.FileMetadata);
    }

    private static bool HasBodyBytes(HttpRequest request)
        => request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding");

    private static bool HasBodyMethod(string method)
        => HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private CapturingReadStream? StartCapture(HttpContext context)
    {
        var request = context.Request;
        if (!HasBodyMethod(request.Method) || !HasBodyBytes(request))
        {
            return null;
        }

        // Every mutating body is hashed as it is read (the intent row keeps the SHA-256 of the whole body); only a text body is also copied.
        var capture = new CapturingReadStream(request.Body, BodyMasker.IsCapturable(request.ContentType) ? maskingOptions.MaxBodyBytes : 0);
        request.Body = capture;
        context.Items[CaptureItemKey] = capture;
        return capture;
    }

    private MaskedBody? DescribeBody(HttpContext context, CapturingReadStream? capture)
    {
        var request = context.Request;
        try
        {
            if (!HasBodyMethod(request.Method) || !HasBodyBytes(request))
            {
                return null;
            }

            if (capture is not null && BodyMasker.IsCapturable(request.ContentType))
            {
                var truncated = capture.IsTruncated || request.ContentLength > maskingOptions.MaxBodyBytes;
                return bodyMasker.MaskText(capture.Captured, truncated, request.ContentType, request.Path.Value);
            }

            if (BodyMasker.IsMultipart(request.ContentType))
            {
                // Only a form the endpoint already parsed: the middleware never reads the upload itself.
                var form = context.Features.Get<IFormFeature>()?.Form;
                return form is null
                    ? new MaskedBody(null, null, false)
                    : bodyMasker.DescribeForm(
                        form.SelectMany(f => f.Value.Select(v => new KeyValuePair<string, string>(f.Key, v ?? string.Empty))),
                        form.Files.Select(f => (f.Name, f.FileName, (string?)f.ContentType, f.Length)),
                        isTruncated: false,
                        request.Path.Value);
            }

            // Binary or unknown type: the content type is kept, the content is not.
            return new MaskedBody(null, null, false);
        }
        catch (Exception)
        {
            // Nothing of an unreadable body is stored; the call itself is still recorded.
            return null;
        }
    }
}

/// <summary>Adds the activity middleware to the pipeline.</summary>
public static class ChronoAuditApplicationBuilderExtensions
{
    /// <summary>
    /// Records every API call in the activity log. Place it as the reference host does: <c>UseForwardedHeaders</c> first (so the recorded IP
    /// is the client's, not the proxy's), then the correlation middleware, then this one, then <c>UseExceptionHandler</c>, routing, CORS,
    /// authentication and authorization. Before the exception handler it sees the final status, including the 500 of a crash; it reads the
    /// user from <c>HttpContext.User</c> after the call, because authentication runs later in the pipeline. Needs <c>AddActivityLog</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder.</returns>
    public static IApplicationBuilder UseChronoAuditActivityLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ChronoAuditActivityMiddleware>();
    }
}
