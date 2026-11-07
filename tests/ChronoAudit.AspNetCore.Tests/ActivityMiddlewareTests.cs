using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class ActivityMiddlewareTests
{
    private static async Task<(ApiRequestLog Log, ApiRequestBody? Body)> OnlyRowAsync(HttpRig rig)
    {
        var log = Assert.Single(await rig.LogsAsync());
        var body = await rig.ReadStoreAsync(db => db.Set<ApiRequestBody>().AsNoTracking().FirstOrDefaultAsync(b => b.ApiRequestLogId == log.Id));
        return (log, body);
    }

    private sealed class ThrowingLogStore : IApiRequestLogStore
    {
        public Task WriteAsync(ApiRequestLogEntry entry, CancellationToken cancellationToken) => throw new InvalidOperationException("the store is down");
    }

    // ---- E07: what is recorded of a body ------------------------------------------------------------------------------------

    [Fact]
    public async Task AJsonPost_StoresTheMaskedBody_AndTheEndpointStillReadsTheWholeBody()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.PostAsync("/api/echo", Who.Me(), rawJson: """{"title":"hello","password":"s3cret"}""");

        Assert.Equal("""{"title":"hello","password":"s3cret"}""", await response.Content.ReadAsStringAsync());
        var (log, body) = await OnlyRowAsync(rig);
        Assert.True(log.HasBody);
        Assert.DoesNotContain("s3cret", body!.Body, StringComparison.Ordinal);
        Assert.Contains("hello", body.Body, StringComparison.Ordinal);
        Assert.Contains(MaskingOptions.Mask, body.Body, StringComparison.Ordinal);
        Assert.StartsWith("application/json", body.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyOver64Kb_IsCut_AndMarkedTruncated_ButTheEndpointReadsAllOfIt()
    {
        await using var rig = await HttpRig.StartAsync();
        var big = "{\"data\":\"" + new string('a', 100_000) + "\"}";

        var response = await rig.PostAsync("/api/echo", Who.Me(), rawJson: big);

        Assert.Equal(big.Length, (await response.Content.ReadAsStringAsync()).Length);
        var (_, body) = await OnlyRowAsync(rig);
        Assert.True(body!.IsTruncated);
        Assert.True(body.Body!.Length <= 64 * 1024);
    }

    [Fact]
    public async Task TheCodeField_IsMaskedOnSecretPathsOnly()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.PostAsync("/api/auth/login", Who.Me(), rawJson: """{"code":"123456"}""");
        await rig.PostAsync("/api/echo", Who.Me(), rawJson: """{"code":"123456"}""");

        var bodies = await rig.ReadStoreAsync(db => db.Set<ApiRequestBody>().AsNoTracking().OrderBy(b => b.ApiRequestLogId).ToListAsync());
        Assert.DoesNotContain("123456", bodies[0].Body, StringComparison.Ordinal);
        Assert.Contains("123456", bodies[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFormBody_IsMaskedByFieldName()
    {
        await using var rig = await HttpRig.StartAsync();
        using var content = new StringContent("user=ann&password=hunter2", Encoding.UTF8, "application/x-www-form-urlencoded");
        var request = rig.Request(HttpMethod.Post, "/api/echo", Who.Me());
        request.Content = content;

        await rig.Client.SendAsync(request);

        var (_, body) = await OnlyRowAsync(rig);
        Assert.DoesNotContain("hunter2", body!.Body, StringComparison.Ordinal);
        Assert.Contains("user=ann", body.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyTheEndpointNeverReads_HasNoText_ButIsMarkedAsHavingABody()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.PostAsync("/api/ignore", Who.Me(), rawJson: """{"never":"read"}""");

        var (log, body) = await OnlyRowAsync(rig);
        Assert.True(log.HasBody);
        Assert.True(body is null || string.IsNullOrEmpty(body.Body));
    }

    [Fact]
    public async Task AGet_HasNoBodyRow()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.GetAsync("/api/hello", Who.Me());

        var (log, body) = await OnlyRowAsync(rig);
        Assert.False(log.HasBody);
        Assert.Null(body);
    }

    [Fact]
    public async Task ABinaryBody_KeepsOnlyItsContentType()
    {
        await using var rig = await HttpRig.StartAsync();
        var request = rig.Request(HttpMethod.Post, "/api/bytes", Who.Me());
        request.Content = new ByteArrayContent([1, 2, 3, 4, 5]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await rig.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (log, body) = await OnlyRowAsync(rig);
        Assert.True(log.HasBody);
        Assert.True(body is null || (body.Body is null && body.ContentType == "application/octet-stream"));
    }

    [Fact]
    public async Task AMultipartTheEndpointParsed_StoresMaskedFieldsAndFileMetadata_NeverTheContent()
    {
        await using var rig = await HttpRig.StartAsync();
        using var content = new MultipartFormDataContent
        {
            { new StringContent("ann"), "user" },
            { new StringContent("hunter2"), "password" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("TOP-SECRET-FILE-CONTENT")), "attachment", "report.pdf" },
        };
        var request = rig.Request(HttpMethod.Post, "/api/form", Who.Me());
        request.Content = content;

        await rig.Client.SendAsync(request);

        var (_, body) = await OnlyRowAsync(rig);
        Assert.NotNull(body);
        Assert.Contains("ann", body.FileMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", body.FileMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-FILE-CONTENT", body.FileMetadata, StringComparison.Ordinal);
        Assert.Contains("report.pdf", body.FileMetadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMultipartTheEndpointNeverParsed_IsRecordedWithoutFields()
    {
        await using var rig = await HttpRig.StartAsync();
        using var content = new MultipartFormDataContent { { new StringContent("ann"), "user" } };
        var request = rig.Request(HttpMethod.Post, "/api/ignore", Who.Me());
        request.Content = content;

        await rig.Client.SendAsync(request);

        var (log, body) = await OnlyRowAsync(rig);
        Assert.True(log.HasBody);
        Assert.True(body is null || (body.Body is null && body.FileMetadata is null));
    }

    [Fact]
    public async Task AnOverlongContentType_IsCutToItsColumn()
    {
        await using var rig = await HttpRig.StartAsync();
        var request = rig.Request(HttpMethod.Post, "/api/echo", Who.Me());
        request.Content = new StringContent("{\"a\":1}", Encoding.UTF8);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; x=" + new string('q', 400));

        await rig.Client.SendAsync(request);

        var (_, body) = await OnlyRowAsync(rig);
        Assert.NotNull(body);
        Assert.True(body.ContentType!.Length <= ApiRequestLogEntry.MaxContentTypeLength);
    }

    // ---- E09, E10 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExcludedPrefixes_AreConfigurable_WithGenericDefaults()
    {
        await using var rig = await HttpRig.StartAsync(configure: o => o.ActivityLog.ExcludedPathPrefixes.Add("/internal"));

        await rig.GetAsync("/health");
        await rig.GetAsync("/internal/status");
        await rig.GetAsync("/api/hello", Who.Me());

        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal("/api/hello", log.Path);
        Assert.Equal(new[] { "/health", "/version", "/swagger", "/hubs" }, new ActivityLogOptions().ExcludedPathPrefixes);
    }

    [Fact]
    public async Task Failures_AreRecordedAs500Or499_WithTheErrorCode()
    {
        await using var rig = await HttpRig.StartAsync();
        await rig.GetAsync("/api/boom", Who.Me());
        await rig.GetAsync("/api/teapot", Who.Me());
        using var cts = new CancellationTokenSource();
        var waiting = rig.Client.SendAsync(rig.Request(HttpMethod.Get, "/api/wait", Who.Me()), cts.Token);
        await Task.Delay(100);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await WaitForRowsAsync(rig, 3);

        var logs = await rig.LogsAsync();
        Assert.Equal(new short[] { 500, 418, 499 }, logs.Select(l => l.StatusCode));
        Assert.Equal("Sample.Teapot", logs[1].ErrorCode);
        Assert.Null(logs[0].ErrorCode);
    }

    // ---- M01 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEndpointKey_IsTheRouteTemplate_AnAnonymousCallHasNoUser_AndAnUnroutedCallHasNoKey()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.GetAsync("/api/items/42", Who.Me("ann"));
        await rig.GetAsync("/api/hello");
        await rig.GetAsync("/nowhere/at/all");

        var logs = await rig.LogsAsync();
        Assert.Equal("api/items/{id}", logs[0].EndpointKey);
        Assert.Equal("ann", logs[0].UserId);
        Assert.Equal("s-ann", logs[0].SessionId);
        Assert.Null(logs[1].UserId);
        Assert.Equal(("/nowhere/at/all", (short)404, null), (logs[2].Path, logs[2].StatusCode, logs[2].EndpointKey));
    }

    [Fact]
    public async Task ACrash_IsRecordedAs500_AndTheExceptionStillReachesTheHost()
    {
        await using var rig = await HttpRig.StartAsync(exceptionHandler: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.GetAsync("/api/boom", Who.Me()));

        Assert.Equal((short)500, Assert.Single(await rig.LogsAsync()).StatusCode);
    }

    [Fact]
    public async Task AFailingStore_NeverBreaksTheCall()
    {
        await using var rig = await HttpRig.StartAsync(services: s => s.AddSingleton<IApiRequestLogStore>(new ThrowingLogStore()));

        var response = await rig.GetAsync("/api/hello", Who.Me());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await rig.LogsAsync());
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongTextIsCutToItsColumns_AndANonAsciiPathIsKeptWhole()
    {
        await using var rig = await HttpRig.StartAsync();
        const string persian = "سلام";

        await rig.GetAsync("/api/ping/" + persian + "?q=" + new string('z', 5000), Who.Me());

        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal("/api/ping/" + persian, log.Path);
        Assert.True(log.QueryString!.Length <= ApiRequestLogEntry.MaxQueryStringLength);
    }

    [Fact]
    public async Task AnAnonymousCallToAProtectedEndpoint_IsRecordedAs401_AndSecretsOfTheQueryAreMasked()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.GetAsync("/chronoaudit/analytics/catalog?access_token=abc123");

        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal(((short)401, null), (log.StatusCode, log.UserId));
        Assert.DoesNotContain("abc123", log.QueryString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDurationAndTheCorrelationId_AreRecorded()
    {
        await using var rig = await HttpRig.StartAsync();
        var correlation = Guid.NewGuid();
        var request = rig.Request(HttpMethod.Get, "/api/hello", Who.Me());
        request.Headers.Add("X-Correlation-Id", correlation.ToString("D"));

        var response = await rig.Client.SendAsync(request);

        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal(correlation, log.CorrelationId);
        Assert.Equal(correlation.ToString("D"), response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.True(log.DurationMs >= 0);
        Assert.Equal(HttpRig.T0, log.OccurredAtUtc);
    }

    [Fact]
    public async Task ADelegatedCall_RecordsBothUsers()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.GetAsync("/api/hello", new Who("assistant", ActAs: "boss"));

        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal(("assistant", "boss"), (log.UserId, log.ActAsUserId));
    }

    private static async Task WaitForRowsAsync(HttpRig rig, int count)
    {
        for (var i = 0; i < 100 && (await rig.LogsAsync()).Count < count; i++)
        {
            await Task.Delay(50);
        }
    }
}

public sealed class MutationIntentTests
{
    private sealed class ThrowingIntentStore : IApiMutationIntentStore
    {
        public Task WriteAsync(ApiMutationIntent intent, CancellationToken cancellationToken) => throw new InvalidOperationException("the store is down");
    }

    private static readonly string EmptySha = Convert.ToHexString(SHA256.HashData([]));

    public static IEnumerable<object[]> Targets() =>
    [
        ["/mvc/orders", "\"title\""],
        ["/api/bound", "\"title\""],
    ];

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task APost_StoresOneIntentBeforeTheAction_WithTheBodyHash_AndTheSameRequestIdAsTheLogRow(string url, string _)
    {
        await using var rig = await HttpRig.StartAsync();
        const string body = """{"title":"chair"}""";

        var response = await rig.PostAsync(url, Who.Me(), rawJson: body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var intent = Assert.Single(await rig.IntentsAsync());
        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), intent.BodySha256);
        Assert.Equal((long)body.Length, intent.BodyLength);
        Assert.Equal(log.RequestId, intent.RequestId);
        Assert.Equal(("POST", "u1"), (intent.Method, intent.UserId));
        Assert.Equal(HttpRig.T0, intent.StartedAtUtc);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AGet_StoresNoIntent(string url, string _)
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.GetAsync(url == "/mvc/orders" ? url : "/api/hello", Who.Me());

        Assert.Empty(await rig.IntentsAsync());
    }

    [Fact]
    public async Task APostWithoutABody_HasTheHashOfZeroBytes()
    {
        await using var rig = await HttpRig.StartAsync();

        await rig.PostAsync("/api/ignore", Who.Me());

        var intent = Assert.Single(await rig.IntentsAsync());
        Assert.Equal((EmptySha, 0L), (intent.BodySha256, intent.BodyLength));
    }

    [Fact]
    public async Task AnEndpointThatReadsTheRawBodyItself_GetsTheHashOfWhatWasReadBeforeTheFilter()
    {
        await using var rig = await HttpRig.StartAsync();

        // The documented limit: the filter never reads or rewinds the body, so a raw body the endpoint reads later is not in the hash.
        var response = await rig.PostAsync("/api/echo", Who.Me(), rawJson: """{"title":"chair"}""");

        Assert.Equal("""{"title":"chair"}""", await response.Content.ReadAsStringAsync());
        var intent = Assert.Single(await rig.IntentsAsync());
        Assert.Equal((EmptySha, 0L), (intent.BodySha256, intent.BodyLength));
    }

    [Fact]
    public async Task AnExcludedPath_StoresNoIntent()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.PostAsync("/health/ping", Who.Me());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await rig.IntentsAsync());
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task WhenTheIntentCannotBeStored_TheActionIsNotRun_AndTheAnswerIs503(string url, string _)
    {
        await using var rig = await HttpRig.StartAsync(services: s => s.AddSingleton<IApiMutationIntentStore>(new ThrowingIntentStore()));

        var response = await rig.PostAsync(url, Who.Me(), rawJson: """{"title":"chair"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = await HttpRig.JsonAsync(response);
        Assert.Equal("ActivityLog.Unavailable", problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("chair", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal(("ActivityLog.Unavailable", (short)503), (log.ErrorCode, log.StatusCode));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TheHash_CoversTheWholeBody_EvenWhenOnly64KbIsCopied(string url, string _)
    {
        await using var rig = await HttpRig.StartAsync();
        var body = "{\"title\":\"" + new string('b', 100_000) + "\"}";

        var response = await rig.PostAsync(url, Who.Me(), rawJson: body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var intent = Assert.Single(await rig.IntentsAsync());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), intent.BodySha256);
        Assert.Equal((long)body.Length, intent.BodyLength);
    }
}
