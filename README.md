# Snail.Toolkit.Minio

Object storage for .NET over Minio and any other S3-compatible server. The API you code against names no
vendor type, failures come back as data, objects stream instead of being buffered, and ranged reads work —
which, as it turns out, is not a given.

```bash
dotnet add package Snail.Toolkit.Minio
```

Targets `net8.0`, `net9.0` and `net10.0`.

## Registration

```csharp
builder.Services.AddMinio(builder.Configuration);
```

That reads the `Minio` section and registers `IObjectStorage`. A second server gets a key of its own:

```csharp
builder.Services.AddMinio(builder.Configuration);
builder.Services.AddKeyedMinio("Archive", builder.Configuration);

public sealed class Reports(
    IObjectStorage storage,
    [FromKeyedServices("Archive")] IObjectStorage archive);
```

A section named something other than the key is bound explicitly:

```csharp
builder.Services.AddKeyedMinio("Archive", builder.Configuration, sectionName: "Storage:Archive");
```

Anything the SDK client needs beyond configuration is applied last, overriding what configuration set:

```csharp
builder.Services.AddMinio(
    builder.Configuration,
    configureClient: client => client.WithProxy(new WebProxy("http://proxy:8080")));
```

Everything is registered as a singleton. A storage client owns connections and is safe to share; there is
no lifetime knob, because a per-request one leaves the container holding every client it ever built.

## Configuration

```json
{
  "Minio": {
    "Endpoint": "play.min.io",
    "AccessKey": "accessKey",
    "SecretKey": "secretKey",
    "Region": "us-east-1",
    "SessionToken": "sessionToken",
    "IsSecure": true,
    "Timeout": "00:00:30",
    "ConnectionLifetime": "00:05:00",
    "MaxConnectionsPerServer": 64,
    "RetryAttempts": 2,
    "RetryDelay": "00:00:00.200",
    "CircuitBreakFailures": 10,
    "CircuitBreakDuration": "00:00:05",
    "SignedUrlLifetime": "00:01:00"
  }
}
```

`Endpoint`, `AccessKey` and `SecretKey` are required. Everything else has a working default.

Settings are validated when the host starts, not when the first request runs. A missing section, an
endpoint carrying a scheme, a zero timeout, a negative retry count — each stops the application with a
message naming the setting, instead of surfacing later as a storage failure that blames the network.

## Reading and writing

```csharp
public sealed class Reports(IObjectStorage storage)
{
    public async Task<string?> SaveAsync(Stream file, string name, CancellationToken token)
    {
        var stored = await storage.PutAsync(
            "reports",
            file,
            new UploadOptions { Name = name, MediaType = "application/pdf" },
            token);

        return stored.Match(
            onSuccess: report => report.Name,
            onFailure: error => null);
    }
}
```

The whole contract:

| Call | Answers |
| --- | --- |
| `PutAsync(bucket, content, options, ct)` | `StoredObject` — what was stored |
| `GetAsync(bucket, name, ct)` | `ObjectContent` — metadata plus a live stream you own |
| `DownloadToAsync(bucket, name, destination, ct)` | `StoredObject`, having copied the bytes |
| `DownloadRangeToAsync(bucket, name, destination, offset, length, ct)` | `StoredObject`, having copied the range |
| `StatAsync(bucket, name, ct)` | `StoredObject`, without reading the bytes |
| `RemoveAsync(bucket, name, ct)` | success, or why not |
| `ExistsAsync(bucket, name, ct)` | whether it is there — a missing object is `false`, not a failure |
| `ListAsync(bucket, options, ct)` | one page of objects, plus a cursor when more follow |
| `EnumerateAsync(bucket, options, ct)` | every object in turn, for walking a whole bucket |
| `SignedUrlAsync(bucket, name, lifetime, ct)` | a URL that reads the object, for handing to a browser |
| `SignedUploadUrlAsync(bucket, name, lifetime, ct)` | a URL that accepts a PUT, so the bytes never pass through you |

`UploadOptions` decides the rest: `Name` (generated when omitted), `MediaType` (derived from the name when
omitted), `Size` (required only for a stream that cannot seek), `AppendExtension`, and `Metadata`.

Metadata written with an object comes back on every call that describes it — `PutAsync`, `StatAsync` and
`GetAsync` — under the names it was written with, without the `x-amz-meta-` the wire adds and removes. What
the protocol defines for itself, the media type and the length, is not repeated there. Values have to be
US-ASCII, because that is what a header can carry; anything else is refused with `InvalidArgument` naming
the entry rather than failing later as a transport error.

## Listing

```csharp
var page = await storage.ListAsync("reports", new ListOptions { Prefix = "2026/", PageSize = 50 });

while (page.Value.HasMore)
{
    page = await storage.ListAsync("reports", new ListOptions { Prefix = "2026/", Cursor = page.Value.Cursor });
}
```

A listing that does not descend rolls names up the way a folder would, and reports them separately:

```csharp
var level = await storage.ListAsync("reports", new ListOptions { IsRecursive = false });

level.Value.Objects;    // top.txt
level.Value.Prefixes;   // 2026/, 2025/
```

For walking a whole bucket, use `EnumerateAsync`. Resuming a paged listing from a cursor costs a walk to
that point — the SDK offers no way to start a listing after a given name, so the server lists from the
beginning and this skips. Fine for a few pages, wrong for a bucket with a million objects. A walk has
nowhere to put a result once it has started, so a failure arrives as the last item of the sequence:

```csharp
await foreach (var item in storage.EnumerateAsync("reports"))
{
    if (!item.TryGetValue(out var stored))
    {
        logger.LogWarning("listing stopped: {Error}", item.Error);
        break;
    }

    Handle(stored);
}
```

## Buckets

```csharp
public sealed class Provisioning(IBuckets buckets)
{
    public Task<StorageResult> EnsureAsync(string bucket) => buckets.CreateAsync(bucket);
}
```

`CreateAsync`, `RemoveAsync` and `ExistsAsync`, resolved separately from `IObjectStorage` because buckets
are made once by whatever provisions the application, while objects are written all day. Creating a bucket
that already exists succeeds; removing one that still holds objects does not.

## Failures are data

Everything a caller can act on comes back as a result. Exceptions are left for what a caller cannot act on.

```csharp
var read = await storage.GetAsync("reports", "q3.pdf", token);

if (!read.TryGetValue(out var content))
{
    return read.Error.Kind switch
    {
        StorageErrorKind.ObjectNotFound => Results.NotFound(),
        StorageErrorKind.AccessDenied => Results.Forbid(),
        _ => Results.Problem(read.Error.Message)
    };
}

await using (content)
{
    return Results.Stream(content.Stream, content.Metadata.MediaType);
}
```

`StorageError` carries the `Kind` to branch on, the `Message`, and — where the server said so — the HTTP
`StatusCode`, the S3 `Code`, and the `Cause` exception. `Match`, `Map` and `OnFailure` are there for
composing without unwrapping, each with a pending overload so `await storage.StatAsync(...).MatchAsync(...)`
reads as one sentence.

A successful result always carries a value: `Success` refuses `null`, so success and "there is something to
read" are the same fact.

The kinds: `Authorization`, `AccessDenied`, `BucketNotFound`, `ObjectNotFound`, `InvalidBucketName`,
`InvalidObjectName`, `Connection`, `Timeout`, `InvalidArgument`, `NotSupported`, `ObjectDisposed`,
`Upstream`, `Unexpected`.

Cancellation is the one thing that is not a result. A cancelled token raises `OperationCanceledException`,
because a caller who cancelled is not asking to be told about it as data.

## Streaming, ranges, and why reads look the way they do

Reads are issued against a URL this library signs with the SDK and then fetches itself. Two reasons, both
measured rather than assumed:

- Minio 7.0.0 turns **every** HTTP 206 answer into `PartialContentException` and delivers no bytes at all.
  Ranged reads through the SDK's read path fail against every server — verified for `WithOffsetAndLength`,
  `WithLength`, a hand-written `Range` header and the file-based path alike, while the same request without
  a range succeeds.
- The SDK's read path hands over a callback rather than a stream, which forces a whole object into memory
  before a caller sees its first byte.

So `GetAsync` gives you a live stream, and `DownloadRangeToAsync` works:

```csharp
await storage.DownloadRangeToAsync("videos", "clip.mp4", response.Body, offset: 1_048_576, length: 65_536);
```

Offsets are 64-bit. The signed URL never leaves the process and is valid for `SignedUrlLifetime`.

A read that fails part-way leaves a seekable destination exactly as it was found — a half-written file is
never left behind for a caller who was told the read failed.

## Retries, tracing, logging

Reads and deletes are retried when the connection is lost, a request times out, or the server answers 408,
429 or 5xx — `RetryAttempts` times, with a delay that doubles and is capped at thirty seconds. An upload is
never retried: its stream may already be partly consumed, and replaying it would store the tail of a file as
the whole of one.

`Retry-After` wins over that schedule. A server that asks for longer than the cap is believed rather than
argued with: the call comes back immediately, carrying the wait it asked for on `StorageError.RetryAfter`,
and the caller decides what to do with a server that will not be ready for five minutes.

After `CircuitBreakFailures` such failures in a row, calls stop going out at all for
`CircuitBreakDuration` — retrying a server that is refusing everything is a share of the load keeping it
down. Then exactly one call is let through to find out whether anything changed, and a success reopens the
gate. Failures that say nothing about the server's health, a missing object above all, are not counted.
Setting `CircuitBreakFailures` to zero turns this off for callers who compose their own resilience.

Every operation opens an activity on the source `Snail.Toolkit.Minio`, tagged with the bucket, the object
and the configuration, and marked with the error kind when it fails:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(MinioObjectStorage.ActivitySourceName));
```

Failures are logged at warning through `ILogger`, retries at debug. No logging configured is fine — the
adapter falls back to a null logger.

## The vendor layer

When you need an SDK feature this library does not model, the SDK is still there — through the same
configuration, the same pools, and with failures still answered as data:

```csharp
public sealed class Buckets(IMinioClient client)
{
    public Task<StorageResult<ObjectStat>> DescribeAsync(string bucket, string name)
        => client.StatObjectAsync(bucket, name);
}
```

`IMinioClients.Create(name)` builds further clients from the same named configuration. Types from the SDK
appear in these signatures on purpose; `IObjectStorage` is the API that does not.

**This layer has no retries and no circuit breaker.** They live in the adapter, so a call made here — or on
an `IMinioClient` resolved from the container — goes to the server once. Code that wants the resilience
takes `IObjectStorage`.

## Coming from an earlier version

The 1.x surface is gone. What it did and where it went:

| Was | Now |
| --- | --- |
| `client.PutStreamAsync(bucket, stream, contentType, name)` | `storage.PutAsync(bucket, stream, new UploadOptions { … })` |
| `client.DownloadObjectAsync(bucket, name)` → `MemoryStream` | `storage.GetAsync(bucket, name)` — streams, never buffers |
| `client.DownloadObjectWithOffsetAndLengthAsync(…)` | `storage.DownloadRangeToAsync(…)` — and it works |
| `MinioResult<T>` / `MinioErrorType` | `StorageResult<T>` / `StorageErrorKind` |
| `IMinioClientFactory.CreateClient(name)` | `IMinioClients.Create(name)` (the SDK owns the old name) |
| `AddMinio(name, configuration)` for a second server | `AddKeyedMinio(name, configuration)` — the old one silently kept the first |
| `AddMinio(…, lifetime: …)` | gone; storage is a singleton |
| listing objects through the SDK | `ListAsync` / `EnumerateAsync` on the port |
| `IMinioClient.MakeBucketAsync(…)` | `IBuckets.CreateAsync(…)` |
| `"SSL": true` | `"IsSecure": true` |
| `"Timeout": 2000` | `"Timeout": "00:00:02"` |

## Running the tests

The suite runs against a real Minio server in Docker through Testcontainers, because a stubbed transport can
only confirm the behaviour it was written to imitate.

```bash
dotnet test
```

Docker has to be running. Everything else — the container, the buckets, the cleanup — takes care of itself.

## License

Snail.Toolkit.Minio is a free and open source project, released under the permissible
[MIT license](LICENSE).
