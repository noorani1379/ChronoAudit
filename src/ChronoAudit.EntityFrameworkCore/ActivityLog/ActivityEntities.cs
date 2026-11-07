namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// One API call. Evidence: every column is set at insert and never changes, except the hash columns, which the hash worker fills once
/// (<see cref="SetHash"/>). It is therefore not <see cref="IAppendOnly"/>, whose guard would reject that one update; the chain detects
/// any later edit. <see cref="RequestId"/> is unique, so a retried write stores one row. The response is recorded as status and error
/// code only. The chain orders the rows by <see cref="ChainIndex"/>.
/// </summary>
public sealed class ApiRequestLog : IExcludedFromAutoAudit
{
    /// <summary>The row id (identity); history paging uses it.</summary>
    public long Id { get; init; }

    /// <summary>The id of the request; unique.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>When the call finished (UTC, offset zero).</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>How long the call took, in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>The signed-in user, when there was one.</summary>
    public string? UserId { get; init; }

    /// <summary>The user who acted on behalf of <see cref="UserId"/>, when it was a delegated call.</summary>
    public string? ActAsUserId { get; init; }

    /// <summary>The session of the call.</summary>
    public string? SessionId { get; init; }

    /// <summary>Ties the call to the audit rows it caused.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The year of <see cref="OccurredAtUtc"/> in the configured calendar; null on a row written without a calendar.</summary>
    public short? CalendarYear { get; init; }

    /// <summary>The month in the configured calendar.</summary>
    public short? CalendarMonth { get; init; }

    /// <summary>The day in the configured calendar.</summary>
    public short? CalendarDay { get; init; }

    /// <summary>The quarter in the configured calendar.</summary>
    public short? CalendarQuarter { get; init; }

    /// <summary>The HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>The request path.</summary>
    public required string Path { get; init; }

    /// <summary>The route template (max 256), so the same endpoint groups together whatever the ids in the path.</summary>
    public string? EndpointKey { get; init; }

    /// <summary>The query string with secrets masked.</summary>
    public string? QueryString { get; init; }

    /// <summary>The HTTP status code.</summary>
    public short StatusCode { get; init; }

    /// <summary>The stable error code of a failed call.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The client address.</summary>
    public string? IpAddress { get; init; }

    /// <summary>The user agent.</summary>
    public string? UserAgent { get; init; }

    /// <summary>True when the request had a body.</summary>
    public bool HasBody { get; init; }

    /// <summary>The masked body kept apart in its own table; written in the same save as the row. Null when none was captured.</summary>
    public ApiRequestBody? Body { get; init; }

    /// <summary>The hash of the previous row of the chain.</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>The hash of this row; empty until the hash worker has processed it.</summary>
    public string? PayloadHash { get; private set; }

    /// <summary>The position in the hash chain; set once together with the hashes.</summary>
    public long? ChainIndex { get; private set; }

    /// <summary>Fills the hash columns once.</summary>
    /// <param name="chainIndex">The position in the chain.</param>
    /// <param name="previousHash">The previous hash.</param>
    /// <param name="payloadHash">The hash of this row.</param>
    /// <exception cref="InvalidOperationException">The row is already hashed.</exception>
    public void SetHash(long chainIndex, string? previousHash, string payloadHash)
    {
        if (!string.IsNullOrEmpty(PayloadHash))
        {
            throw new InvalidOperationException("This API request log has already been hashed and cannot be altered.");
        }

        ChainIndex = chainIndex;
        PreviousHash = previousHash;
        PayloadHash = payloadHash;
    }
}

/// <summary>The masked request body of one API call (1:1 with the log row, kept apart so the log table stays narrow). Insert-only.</summary>
public sealed class ApiRequestBody : IAppendOnly, IExcludedFromAutoAudit
{
    /// <summary>The id of the log row this body belongs to.</summary>
    public long ApiRequestLogId { get; init; }

    /// <summary>The content type of the body.</summary>
    public string? ContentType { get; init; }

    /// <summary>The masked body, at most 64 KB.</summary>
    public string? Body { get; init; }

    /// <summary>True when the body was longer than what is stored.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>For a multipart request: field names and masked values, file name, size and content type of each file (never the content).</summary>
    public string? FileMetadata { get; init; }
}

/// <summary>
/// The INTENT of a mutating API call (POST, PUT, PATCH, DELETE), written and saved BEFORE the action runs. The result row
/// (<see cref="ApiRequestLog"/>) is written after the call, so a crash or kill in between would lose it; this row survives, and a row
/// with no result after a few minutes is reported as "no result". <see cref="RequestId"/> ties the two rows and is unique.
/// </summary>
public sealed class ApiMutationIntent : IExcludedFromAutoAudit
{
    /// <summary>The row id (identity).</summary>
    public long Id { get; init; }

    /// <summary>The id of the request; unique; the result row carries the same id.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>When the call started (UTC, offset zero).</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>The signed-in user.</summary>
    public string? UserId { get; init; }

    /// <summary>The user who acted on behalf of <see cref="UserId"/>.</summary>
    public string? ActAsUserId { get; init; }

    /// <summary>The session of the call.</summary>
    public string? SessionId { get; init; }

    /// <summary>Ties the call to the audit rows it causes.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The HTTP method.</summary>
    public required string Method { get; init; }

    /// <summary>The request path.</summary>
    public required string Path { get; init; }

    /// <summary>The route template, so the same endpoint groups together whatever the ids in the path.</summary>
    public string? EndpointKey { get; init; }

    /// <summary>SHA-256 (hex) of the request body bytes read until the action started.</summary>
    public required string BodySha256 { get; init; }

    /// <summary>The length of the body in bytes.</summary>
    public long BodyLength { get; init; }

    /// <summary>The hash of the previous row of the chain.</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>The hash of this row; empty until the hash worker has processed it.</summary>
    public string? PayloadHash { get; private set; }

    /// <summary>The position in the hash chain; set once together with the hashes.</summary>
    public long? ChainIndex { get; private set; }

    /// <summary>Fills the hash columns once.</summary>
    /// <param name="chainIndex">The position in the chain.</param>
    /// <param name="previousHash">The previous hash.</param>
    /// <param name="payloadHash">The hash of this row.</param>
    /// <exception cref="InvalidOperationException">The row is already hashed.</exception>
    public void SetHash(long chainIndex, string? previousHash, string payloadHash)
    {
        if (!string.IsNullOrEmpty(PayloadHash))
        {
            throw new InvalidOperationException("This API mutation intent has already been hashed and cannot be altered.");
        }

        ChainIndex = chainIndex;
        PreviousHash = previousHash;
        PayloadHash = payloadHash;
    }
}

/// <summary>Where intent rows are kept. A write failure must reach the caller: the filter refuses the call (fail-closed) when the intent cannot be stored.</summary>
public interface IApiMutationIntentStore
{
    /// <summary>Stores the intent. Idempotent on <see cref="ApiMutationIntent.RequestId"/>. Any other failure is thrown.</summary>
    /// <param name="intent">The intent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the intent is stored.</returns>
    Task WriteAsync(ApiMutationIntent intent, CancellationToken cancellationToken);
}

/// <summary>
/// Where the API activity log is written. Independent of the request: it uses a unit of work of its own, so a failed or half-done
/// business save can never block or roll back the evidence row, and the evidence row can never disturb the request.
/// </summary>
public interface IApiRequestLogStore
{
    /// <summary>
    /// Inserts the row. Idempotent on <see cref="ApiRequestLogEntry.RequestId"/>: writing the same request again (a retry) stores one row
    /// and says nothing. Any other failure is thrown to the caller, which decides what a lost row means.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the row is stored.</returns>
    Task WriteAsync(ApiRequestLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// What the activity middleware knows about one finished API call. <see cref="Create"/> is the only way to build it: it cuts every text
/// to the width of its column, so a long path or user agent can never make the insert fail (a failed insert is a lost evidence row).
/// </summary>
public sealed record ApiRequestLogEntry
{
    /// <summary>The widest HTTP method.</summary>
    public const int MaxMethodLength = 8;

    /// <summary>The widest path.</summary>
    public const int MaxPathLength = 512;

    /// <summary>The widest endpoint key.</summary>
    public const int MaxEndpointKeyLength = 256;

    /// <summary>The widest query string.</summary>
    public const int MaxQueryStringLength = 2048;

    /// <summary>The widest error code.</summary>
    public const int MaxErrorCodeLength = 128;

    /// <summary>The widest client address.</summary>
    public const int MaxIpAddressLength = 64;

    /// <summary>The widest user agent.</summary>
    public const int MaxUserAgentLength = 512;

    /// <summary>The widest content type.</summary>
    public const int MaxContentTypeLength = 128;

    /// <summary>The widest user, delegate or session id.</summary>
    public const int MaxIdLength = 64;

    /// <summary>The id of the request.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>When the call finished.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>The duration in milliseconds.</summary>
    public required int DurationMs { get; init; }

    /// <summary>The signed-in user.</summary>
    public string? UserId { get; init; }

    /// <summary>The user who acted on behalf of <see cref="UserId"/>.</summary>
    public string? ActAsUserId { get; init; }

    /// <summary>The session.</summary>
    public string? SessionId { get; init; }

    /// <summary>The correlation id.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The HTTP method.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>The path.</summary>
    public required string Path { get; init; }

    /// <summary>The route template.</summary>
    public string? EndpointKey { get; init; }

    /// <summary>The masked query string.</summary>
    public string? QueryString { get; init; }

    /// <summary>The HTTP status code.</summary>
    public required short StatusCode { get; init; }

    /// <summary>The error code of a failed call.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The client address.</summary>
    public string? IpAddress { get; init; }

    /// <summary>The user agent.</summary>
    public string? UserAgent { get; init; }

    /// <summary>True when the request had a body.</summary>
    public bool HasBody { get; init; }

    /// <summary>The masked body (at most 64 KB), or null when none was captured.</summary>
    public string? Body { get; init; }

    /// <summary>The content type of the body.</summary>
    public string? BodyContentType { get; init; }

    /// <summary>True when the body was cut.</summary>
    public bool BodyIsTruncated { get; init; }

    /// <summary>Masked form fields and file names/sizes of a multipart request (JSON), or null.</summary>
    public string? FileMetadata { get; init; }

    /// <summary>True when a body row is stored with the log row.</summary>
    public bool HasStoredBody => Body is not null || FileMetadata is not null || BodyContentType is not null;

    /// <summary>Builds an entry, cutting every text to the width of its column.</summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="occurredAtUtc">When the call finished.</param>
    /// <param name="duration">How long it took.</param>
    /// <param name="httpMethod">The method.</param>
    /// <param name="path">The path.</param>
    /// <param name="statusCode">The status code.</param>
    /// <param name="userId">The user.</param>
    /// <param name="actAsUserId">The delegate.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="correlationId">The correlation id.</param>
    /// <param name="endpointKey">The route template.</param>
    /// <param name="queryString">The masked query string.</param>
    /// <param name="errorCode">The error code.</param>
    /// <param name="ipAddress">The client address.</param>
    /// <param name="userAgent">The user agent.</param>
    /// <param name="hasBody">Whether the request had a body.</param>
    /// <param name="body">The masked body.</param>
    /// <param name="bodyContentType">The body content type.</param>
    /// <param name="bodyIsTruncated">Whether the body was cut.</param>
    /// <param name="fileMetadata">The multipart description.</param>
    /// <returns>The entry.</returns>
    public static ApiRequestLogEntry Create(
        Guid requestId,
        DateTimeOffset occurredAtUtc,
        TimeSpan duration,
        string? httpMethod,
        string? path,
        int statusCode,
        string? userId = null,
        string? actAsUserId = null,
        string? sessionId = null,
        Guid? correlationId = null,
        string? endpointKey = null,
        string? queryString = null,
        string? errorCode = null,
        string? ipAddress = null,
        string? userAgent = null,
        bool hasBody = false,
        string? body = null,
        string? bodyContentType = null,
        bool bodyIsTruncated = false,
        string? fileMetadata = null) => new()
        {
            RequestId = requestId,
            OccurredAtUtc = occurredAtUtc,
            DurationMs = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue),
            UserId = Cut(userId, MaxIdLength),
            ActAsUserId = Cut(actAsUserId, MaxIdLength),
            SessionId = Cut(sessionId, MaxIdLength),
            CorrelationId = correlationId,
            HttpMethod = Cut(string.IsNullOrWhiteSpace(httpMethod) ? "?" : httpMethod, MaxMethodLength)!,
            Path = Cut(string.IsNullOrEmpty(path) ? "/" : path, MaxPathLength)!,
            EndpointKey = Cut(endpointKey, MaxEndpointKeyLength),
            QueryString = Cut(queryString, MaxQueryStringLength),
            StatusCode = (short)Math.Clamp(statusCode, 0, short.MaxValue),
            ErrorCode = Cut(errorCode, MaxErrorCodeLength),
            IpAddress = Cut(ipAddress, MaxIpAddressLength),
            UserAgent = Cut(userAgent, MaxUserAgentLength),
            HasBody = hasBody,
            Body = body,
            BodyContentType = Cut(bodyContentType, MaxContentTypeLength),
            BodyIsTruncated = bodyIsTruncated,
            FileMetadata = fileMetadata,
        };

    private static string? Cut(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
