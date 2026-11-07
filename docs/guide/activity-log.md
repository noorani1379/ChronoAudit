# API activity log and mutation intents

The activity log answers "who called what, when, with which result" for every API call, and keeps a masked copy of the body of calls that change data. The mutation intent answers a harder question: "did this change request arrive and not finish?". Both are tamper-evident: their rows are chained with the same HMAC as the audit rows (see [hash chain and anchors](hash-chain-and-anchors.md)).

Needs `UseStore<T>()` and `AddActivityLog()` ([getting started](getting-started.md)).

## 1. The middleware and where it goes

```csharp
using ChronoAudit.AspNetCore;

public static class PipelineExample
{
    public static void Configure(WebApplication app)
    {
        app.UseForwardedHeaders();          // first, so the recorded IP is the client's, not the proxy's
        app.UseChronoAuditActivityLog();    // before the exception handler, routing and authentication
        app.UseExceptionHandler("/error");
        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapChronoAudit("chronoaudit");
    }
}
```

Why this order:

- **Before `UseExceptionHandler`**, the middleware sees the final status, including the 500 of a crash. The exception still reaches your handler; the middleware never swallows it.
- **Before `UseAuthentication` / `UseAuthorization`**, calls that are refused (401, 403) are recorded too. The middleware reads `HttpContext.User` *after* the call, when authentication has run, so the user is known.
- It writes the row after the call, with `CancellationToken.None` (the client may have gone away), and a failing store is logged and never breaks the call.

Paths that start with one of `ChronoAudit:ActivityLog:ExcludedPathPrefixes` are not recorded (default `/health`, `/version`, `/swagger`, `/hubs`; setting the list replaces the defaults).

## 2. What is recorded

One row per call in `ApiRequestLogs`:

| Field | Value |
|---|---|
| `RequestId`, `OccurredAtUtc`, `DurationMs` | a new id per call, the start time, the time taken |
| `HttpMethod`, `Path`, `QueryString` | the query string has secret values masked |
| `EndpointKey` | the route template (`books/{id}`), so calls to one endpoint group together whatever the ids; null for an unrouted call |
| `StatusCode`, `ErrorCode` | the final status (500 for a crash, 499 when the client left before an answer); the stable error code ChronoAudit's endpoints returned |
| `UserId`, `ActAsUserId`, `SessionId` | from the claims (`ChronoAuditAspNetCoreOptions` names the claim types) |
| `CorrelationId` | the id of `X-Correlation-Id`, shared with the audit rows the call caused |
| `IpAddress`, `UserAgent` | the client |
| `HasBody` | a body was sent |

A call that changes data (POST, PUT, PATCH, DELETE) with a text body also gets an `ApiRequestBodies` row with the **masked** body and its content type. The text content types are JSON, XML, `x-www-form-urlencoded`, `text/*` and `+json` / `+xml`. A binary body is not stored. A multipart upload stores the form fields (masked) and, for each file, only its name, file name, content type and size, never the content. The body is read as the endpoint reads it, never earlier: the middleware does not buffer or rewind the request.

Text over a column's width is cut and numbers are clamped, so a hostile request cannot make the log write fail.

## 3. Masking

Values are masked in bodies, query strings and form fields by **name**. Names are compared in lower case with `_`, `-` and spaces removed, so `refresh-token`, `refresh_token` and `RefreshToken` are one name. A value is replaced by `***MASKED***` when its name is

- one of `SecretNames` (default: `otp`, `otpcode`, `captchacode`, `apikey`, `twofactorcode`, `pin`, `passcode`, `cvv2`, `cvv`, `authorization`, `cookie`, `setcookie`),
- contains one of `SecretNameContains` (default: `password`),
- or ends with one of `SecretNameSuffixes` (default: `token`, `secret`).

The field `code` is a one-time secret only under the paths in `CodeIsSecretPathFragments` (default `/auth/`, `/otp/`, `/sessions/`); elsewhere it is an ordinary business value. A body longer than `MaxBodyBytes` (default 64 KiB) is cut and marked truncated; a cut body is masked by pattern, so a secret in it is hidden even though the JSON is broken.

```json
{
  "ChronoAudit": {
    "Masking": {
      "MaxBodyBytes": 65536,
      "CodeIsSecretPathFragments": [ "/auth/", "/otp/", "/sessions/", "/reset/" ]
    },
    "ActivityLog": {
      "ExcludedPathPrefixes": [ "/health", "/swagger" ],
      "HashIntervalSeconds": 5,
      "HashBatchSize": 500,
      "IncompleteIntentAgeMinutes": 5
    }
  }
}
```

`SecretNames`, `SecretNameContains` and `SecretNameSuffixes` are sets you extend in code through the `configure` callback of `AddChronoAudit` (`options.Masking.SecretNames.Add("iban")`); the lists in configuration are for the path fragments and prefixes. These are *name-based* rules for API traffic. For values in your database tables use `[SensitiveData]` and the personal-data lists ([personal data](personal-data.md)).

## 4. Mutation intents

A crash between "the server received the change request" and "the server answered" leaves no trace in an ordinary log. A mutation intent is written **before** the handler runs and carries the same `RequestId` as the result row the activity middleware writes afterwards:

- `ApiMutationIntents` row: `RequestId`, `StartedAtUtc`, user, method, path, endpoint key, the **SHA-256 of the request body** and its length.
- The result is the `ApiRequestLogs` row with that `RequestId`.
- An intent without a result row older than `IncompleteIntentAgeMinutes` (default 5) is *incomplete*: the process died, or the answer was never recorded. `GET /chronoaudit/activity/intents/incomplete` lists them so you can reconcile or alert.

An intent is not an idempotency key: it does not stop a retry. It tells you that a request arrived and whether it finished; the hash lets you prove which body it carried.

Register and apply it:

```csharp
using ChronoAudit.AspNetCore;

public static class IntentExample
{
    public static void Register(IServiceCollection services)
        => services.AddChronoAuditMutationIntents();   // MVC: a global filter on every action that writes

    public static void MapBooks(WebApplication app)
    {
        // Minimal APIs: add the filter to a group (or to a single endpoint).
        var books = app.MapGroup("/books")
            .RequireAuthorization("chronoaudit")
            .WithChronoAuditMutationIntents();

        books.MapPost("", (CreateBook request) => Results.Created("/books/1", request));
    }

    public sealed record CreateBook(string Title, string Author);
}
```

Behavior:

- Only POST, PUT, PATCH and DELETE are recorded; excluded paths are skipped.
- If the intent cannot be written, **the handler does not run**: the caller gets `503 ActivityLog.Unavailable`. A change that cannot be recorded is not made.
- The hash covers every byte the request had read when the filter ran. Model binding of a JSON or form body reads all of it, so for an ordinary endpoint the hash is of the whole body. The filter never reads, buffers or rewinds the body itself. An endpoint that reads the raw body by itself *after* the filter gets a hash of only the bytes read before it.
- A body-less request hashes zero bytes (`E3B0C442...`, the SHA-256 of nothing).

## 5. Reading the log

- Over HTTP: `GET /chronoaudit/activity/search`, `export.csv`, `statistics`, `requests/{logId}/body`, `intents/incomplete` ([HTTP endpoints](http-endpoints.md)). A caller without the All scope sees only their own calls. Reading a stored body is allowed only for the All scope and is itself audited.
- In code: `IActivitySearch` (search, count, view a body, incomplete intents) and `IActivityStatistics`.
- Analytics: the `activity` subject lets you group and count calls ([analytics](analytics.md)).
