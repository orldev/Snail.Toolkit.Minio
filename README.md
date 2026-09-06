# Snail.Toolkit.Minio

Object storage for .NET over MinIO and any S3-compatible server.

Inject `IObjectStorage` and get on with it: upload, download, stream, list, copy and share objects, and
let a bucket expire what it holds. Failures come
back as data instead of exceptions, reads stream instead of buffering, ranged reads work, and retries, a
circuit breaker, tracing and logging are already wired in.

```bash
dotnet add package Snail.Toolkit.Minio
```

---

## Quick start

**1. Configure** — `appsettings.json`:

```json
{
  "Minio": {
    "Endpoint": "localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin",
    "IsSecure": false
  }
}
```

**2. Register** — `Program.cs`:

```csharp
builder.Services.AddMinio(builder.Configuration);
```

**3. Use** — anywhere:

```csharp
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Ports;

public sealed class Reports(IObjectStorage storage, ILogger<Reports> logger)
{
    public async Task<string?> SaveAsync(Stream file, string name, CancellationToken token)
    {
        var stored = await storage.PutAsync(
            "reports",
            file,
            new UploadOptions { Name = name, MediaType = "application/pdf" },
            token);

        if (!stored.TryGetValue(out var report))
        {
            logger.LogWarning("upload failed: {Error}", stored.Error);

            return null;
        }

        return report.Name;
    }
}
```

If the settings are wrong — a missing section, an endpoint with `https://` in it, a zero timeout — the
application does not start, and the message names the setting.

---

## How to do the usual things

### Upload what a browser sent

A request body cannot seek, so state its size:

```csharp
app.MapPost("/reports", async (IFormFile file, IObjectStorage storage, CancellationToken token) =>
{
    await using var content = file.OpenReadStream();

    var stored = await storage.PutAsync(
        "reports",
        content,
        new UploadOptions
        {
            Name = file.FileName,
            MediaType = file.ContentType,
            Size = file.Length
        },
        token);

    return stored.Match(
        onSuccess: report => Results.Created($"/reports/{report.Name}", report),
        onFailure: error => Results.Problem(error.Message));
});
```

Leave `Name` out and a name is generated for you. Leave `MediaType` out and it is derived from the name.

### Send a file to the client without buffering it

```csharp
app.MapGet("/reports/{name}", async (string name, IObjectStorage storage, CancellationToken token) =>
{
    var read = await storage.GetAsync("reports", name, token);

    if (!read.TryGetValue(out var content))
        return read.Error.Kind switch
        {
            StorageErrorKind.ObjectNotFound => Results.NotFound(),
            StorageErrorKind.AccessDenied => Results.Forbid(),
            _ => Results.Problem(read.Error.Message)
        };

    return Results.Stream(content.Stream, content.Metadata.MediaType, name);
});
```

`content.Stream` is the connection, not a copy in memory — an object larger than your RAM costs a buffer.
You own it: dispose it, and the connection goes with it.

### Serve part of a file

```csharp
await storage.DownloadRangeToAsync("videos", "clip.mp4", response.Body, offset: 1_048_576, length: 65_536);
```

Offsets are 64-bit, so ranges past 2 GB work. If the copy fails half way, a seekable destination is rewound
— you never get a half-written file that claims to have failed.

### Copy into a file, a buffer, anything writable

```csharp
await using var file = File.Create(path);

var read = await storage.DownloadToAsync("reports", "q3.pdf", file, token);
```

### Move an object without its bytes coming to you

```csharp
await storage.CopyAsync("uploads", "tmp/9f2c", "reports", "q3.pdf");
await storage.RemoveAsync("uploads", "tmp/9f2c");
```

The server does the copying, so a staged file becomes a kept one for the price of a request rather than of
its own size twice over. Copying onto a name that is taken replaces it. Nothing is reported about the copy:
the server answers with a tag and a moment, the SDK discards them, and asking again on every call would
make everyone pay for what few callers read — `StatAsync` is there for those who do.

### Let the browser do the transfer

```csharp
var link = await storage.SignedUrlAsync("reports", "q3.pdf", TimeSpan.FromMinutes(10));

// upload straight from the browser, bytes never touch this application
var slot = await storage.SignedUploadUrlAsync("uploads", $"{Guid.NewGuid():N}.jpg", TimeSpan.FromMinutes(5));
```

Signing happens locally; nothing is asked of the server. The URL carries your credentials' authority while
it lives, so hand it to a browser, not to a log.

### List a folder

```csharp
var level = await storage.ListAsync("reports", new ListOptions { IsRecursive = false });

level.Value.Objects;    // top-level files
level.Value.Prefixes;   // "2026/", "2025/" — what folders look like in a store that has none
```

Paged:

```csharp
var page = await storage.ListAsync("reports", new ListOptions { Prefix = "2026/", PageSize = 50 });

while (page.Value.HasMore)
{
    Render(page.Value.Objects);

    page = await storage.ListAsync(
        "reports",
        new ListOptions { Prefix = "2026/", PageSize = 50, Cursor = page.Value.Cursor });
}
```

### Walk a whole bucket

```csharp
await foreach (var item in storage.EnumerateAsync("reports", cancellationToken: token))
{
    if (!item.TryGetValue(out var stored))
    {
        logger.LogWarning("listing stopped: {Error}", item.Error);

        break;
    }

    Handle(stored);
}
```

Use this rather than paging through a large bucket: resuming from a cursor costs a walk to that point,
because the SDK offers no way to start a listing after a given name.

### Ask whether something is there

```csharp
if ((await storage.ExistsAsync("reports", name, token)).Value)
{
    // …
}
```

A missing object answers `false`. Only a question that could not reach the server is a failure.

### Store and read your own metadata

```csharp
await storage.PutAsync("reports", content, new UploadOptions
{
    Name = "q3.pdf",
    Metadata = new Dictionary<string, string> { ["author"] = "orldev", ["build"] = "42" }
});

var stat = await storage.StatAsync("reports", "q3.pdf");

stat.Value.Metadata["author"];   // orldev
```

Values have to be US-ASCII — that is all a header can carry. Anything else is refused with
`InvalidArgument` naming the entry, rather than failing later as a transport error.

### Make and remove buckets

```csharp
public sealed class Provisioning(IBuckets buckets)
{
    public Task<StorageResult> EnsureAsync(string bucket) => buckets.CreateAsync(bucket);
}
```

Creating one that exists succeeds. Removing one that still holds objects does not.

### Let a bucket clean up after itself

```csharp
await buckets.SetExpiryAsync("uploads",
[
    new ExpiryRule { Id = "staged", Prefix = "tmp/", Days = 1 }
]);
```

Some objects outlive every process that knew about them — a file staged for an answer that never came, and
no request left to clear it. Expiry is the server's own housekeeping, and the only thing that reaches them.

The call takes every rule the bucket is to have, not one to add, because that is what the protocol does: a
server given one rule keeps only that rule. An API shaped as "add this" would quietly delete the rest. An
empty set clears them, and `GetExpiryAsync` reads them back — a bucket that expires nothing answers with no
rules rather than with a failure.

Days, not a `TimeSpan`: whole days is the granularity S3 offers, and a finer promise would be a lie. A
bucket that does not exist is refused here, because the server accepts rules for one and keeps nothing.

### Talk to two servers

```csharp
builder.Services.AddMinio(builder.Configuration);                  // section "Minio"
builder.Services.AddKeyedMinio("Archive", builder.Configuration);  // section "Archive"

public sealed class Reports(
    IObjectStorage storage,
    [FromKeyedServices("Archive")] IObjectStorage archive);
```

A section named differently from the key is bound explicitly:

```csharp
builder.Services.AddKeyedMinio("Archive", builder.Configuration, sectionName: "Storage:Archive");
```

---

## Handling failures

Every call answers with a result. Read it with `TryGetValue`, `Match`, or by checking `IsSuccess`:

```csharp
var stored = await storage.PutAsync("reports", content, options, token);

var response = stored.Match(
    onSuccess: report => Results.Ok(report),
    onFailure: error => error.Kind switch
    {
        StorageErrorKind.BucketNotFound => Results.NotFound(),
        StorageErrorKind.AccessDenied => Results.Forbid(),
        StorageErrorKind.InvalidArgument => Results.BadRequest(error.Message),
        _ => Results.Problem(error.Message)
    });
```

`StorageError` carries the `Kind` to branch on, a `Message`, and — where the server said so — the HTTP
`StatusCode`, the S3 `Code`, the `Cause` exception and the `RetryAfter` it asked for.

| Kind | Means |
| --- | --- |
| `ObjectNotFound`, `BucketNotFound` | it is not there |
| `AccessDenied`, `Authorization` | credentials rejected, or not allowed |
| `InvalidBucketName`, `InvalidObjectName`, `InvalidArgument` | the request was wrong |
| `NotSupported` | the operation cannot be done as asked — a stream that cannot seek and states no size |
| `Connection`, `Timeout` | the server could not be reached, or did not answer in time |
| `Upstream` | the server failed and said so |
| `Unexpected` | something else entirely |
| `ObjectDisposed` | a stream handed in was already closed |

`Match`, `Map` and `OnFailure` compose without unwrapping, each with a pending overload:

```csharp
var size = await storage.StatAsync("reports", name, token)
    .MatchAsync(onSuccess: report => report.Size, onFailure: _ => 0L);
```

Cancellation is the one thing that is not a result: a cancelled token raises `OperationCanceledException`,
because a caller who cancelled is not asking to be told about it as data.

---

## Configuration

| Setting | Default | What it does |
| --- | --- | --- |
| `Endpoint` | — | `host` or `host:port`, no scheme. Required |
| `AccessKey`, `SecretKey` | — | credentials. Required |
| `IsSecure` | `true` | whether to speak https |
| `Region`, `SessionToken` | — | for AWS and temporary credentials |
| `Timeout` | client default | how long one request may take, `"00:00:30"` |
| `ConnectionLifetime` | `00:05:00` | how long a pooled connection is reused |
| `MaxConnectionsPerServer` | unbounded | ceiling on simultaneous connections |
| `RetryAttempts` | `2` | retries for reads and deletes |
| `RetryDelay` | `00:00:00.200` | first wait; it doubles, capped at 30 s |
| `CircuitBreakFailures` | `10` | failures in a row before calls stop going out; `0` turns it off |
| `CircuitBreakDuration` | `00:00:05` | how long they stop |
| `SignedUrlLifetime` | `00:01:00` | default life of a signed URL |

Settings are validated when the host starts, not when the first request runs.

---

## What happens without you asking

**Retries.** Reads and deletes are retried when the connection is lost, the request times out, or the
server answers 408, 429 or 5xx. Uploads are never retried: the stream may already be partly consumed.
`Retry-After` wins over the schedule, and a server asking for longer than thirty seconds is believed — the
call returns immediately, carrying the wait it asked for.

**A circuit breaker.** After `CircuitBreakFailures` such failures in a row, calls stop going out for
`CircuitBreakDuration`, then one is let through to see whether anything changed. A missing object is not
counted: that is an answer, not the server's health.

**Tracing and logs.** Every operation opens an activity on the source `Snail.Toolkit.Minio`, tagged with
the bucket, the object and the configuration:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(MinioObjectStorage.ActivitySourceName));
```

Real failures log at warning, ordinary ones — a missing object — at debug. No logging configured is fine.

**Streaming reads.** Reads are issued against a URL this library signs and fetches itself, rather than
through the SDK's read path. Two measured reasons: MinIO's client turns every HTTP 206 answer into an
exception, so ranged reads are impossible through it, and its read path buffers a whole object before you
see a byte.

---

## When you need the SDK

Anything this library does not model is still reachable, over the same configuration and connection pools,
with failures still answered as data:

```csharp
public sealed class Tags(IMinioClient client)
{
    public Task<StorageResult<ObjectStat>> DescribeAsync(string bucket, string name)
        => client.StatObjectAsync(bucket, name);
}
```

`IMinioClients.Create(name)` builds further clients from a named configuration.

**This layer has no retries and no circuit breaker** — they live in the adapter. A call made here goes to
the server once. Code that wants the resilience takes `IObjectStorage`.

---

## Coming from 1.x

| Was | Now |
| --- | --- |
| `client.PutStreamAsync(bucket, stream, contentType, name)` | `storage.PutAsync(bucket, stream, new UploadOptions { … })` |
| `client.DownloadObjectAsync(bucket, name)` → `MemoryStream` | `storage.GetAsync(bucket, name)` — streams, never buffers |
| `client.DownloadObjectWithOffsetAndLengthAsync(…)` | `storage.DownloadRangeToAsync(…)` — and it works |
| listing through the SDK | `storage.ListAsync(…)` / `storage.EnumerateAsync(…)` |
| `client.MakeBucketAsync(…)` | `buckets.CreateAsync(…)` |
| `MinioResult<T>` / `MinioErrorType` | `StorageResult<T>` / `StorageErrorKind` |
| `IMinioClientFactory.CreateClient(name)` | `IMinioClients.Create(name)` (the SDK owns the old name) |
| `AddMinio(name, configuration)` for a second server | `AddKeyedMinio(name, configuration)` |
| `AddMinio(…, lifetime: …)` | gone; storage is a singleton |
| `"SSL": true` | `"IsSecure": true` |
| `"Timeout": 2000` | `"Timeout": "00:00:02"` |

---

## Running the tests

The suite runs against a real MinIO server in Docker through Testcontainers, because a stubbed transport
can only confirm the behaviour it was written to imitate.

```bash
dotnet test
```

Docker has to be running. The container, the buckets and the cleanup take care of themselves.

## License

Released under the [MIT license](LICENSE).
