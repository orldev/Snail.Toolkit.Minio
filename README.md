# Minio

Extension for the framework `Minio` - because why use simple storage when you can have complicated dependency injection with **actual error handling**?

## Connecting the configuration (pick your poison)

```c# 
// Option 1: The basic "I hope this works" approach
builder.Services.AddMinio(builder.Configuration);
```

```c# 
// Option 2: The "I have multiple storage accounts and I like pain" approach
builder.Services.AddMinio("Minio1", builder.Configuration);
```

```c# 
// Option 3: The "I read the documentation (unlike you)" approach
builder.Services.AddMinio(builder.Configuration, client => client
    .WithProxy(new WebProxy("http://proxy:8080"))   // Because corporate firewalls are fun!
    .WithRetryPolicy(retryHandler)                  // For when hope is not a strategy
    .WithTimeout(30_000));                          // Patience, but bounded
```

> Only the **first** `AddMinio` call registers the injectable `IMinioClient`. Register as many named
> configurations as you like, then reach the extra ones through
> `IMinioClientFactory.CreateClient("Name")` — a second `AddMinio` will not replace the first client
> (nor its lifetime).

## Sample configuration appsettings.json for using Minio

The section name matches the name you pass to `AddMinio`, and defaults to `Minio`.

```json
{
  "Minio": {
    "Endpoint": "play.min.io",      // required — where your data goes to hide
    "AccessKey": "accessKey",       // required — the key you'll inevitably commit to GitHub
    "SecretKey": "secretKey",       // required — the secret you'll rotate every 90 days (or not)
    "Region": "region",             // - optional (like most meetings)
    "SessionToken": "sessionToken", // - optional (temporary, like your motivation)
    "Timeout": 2000,                // - optional (how long to wait before giving up)
    "SSL": true                     // - optional, defaults to true (keep it that way)
  }
}
```

`Endpoint`, `AccessKey`, and `SecretKey` are required: the underlying Minio client refuses anonymous
access, so a missing value fails fast with a message naming the offending configuration section.

## Actually Useful Error Handling!

Tired of exceptions crashing your party? Meet `MinioResult` - because sometimes failure is an option!

### Civilized Error Handling
```csharp
// Upload files like a pro
var result = await minioClient.PutStreamAsync("bucket", stream, "image/png", "object");
result.Match(
    onSuccess: response => Console.WriteLine($"Uploaded! ETag: {response.Etag}"),
    onFailure: (errorType, message) => Console.WriteLine($"Failed with {errorType}: {message}")
);

// Download without the drama — the stream comes back rewound and ready to read
var downloadResult = await minioClient.DownloadObjectAsync("bucket", "object");
if (downloadResult.TryGetValue(out var stream))
{
    using (stream)
    {
        // Do something amazing with your stream
    }
}
else if (downloadResult.ErrorType == MinioErrorType.ObjectNotFound)
{
    Console.WriteLine("The object is on a coffee break");
}

// Remove objects safely
var removeResult = await minioClient.RemoveObjectAsync("bucket", "object");
if (!removeResult.IsSuccess)
{
    _logger.LogWarning("Delete failed, but at least we didn't crash!");
}
```

### Advanced Error Handling (For Overachievers):
```csharp
// Pattern matching FTW!
var result = await minioClient.StatObjectAsync("bucket", "object")
    .Match(
        onSuccess: stat => new { Exists = true, Size = stat.Size },
        onFailure: (errorType, message) => new { Exists = false, Error = errorType }
    );

// Functional programming magic
var fileInfo = await minioClient.GetObjectAsync("bucket", "file.txt")
    .Match(
        onSuccess: objectStat => $"File size: {objectStat.Size} bytes",
        onFailure: (errorType, message) => $"Error: {errorType}"
    );

// Chain operations like a boss
var operation = await minioClient.PutStreamAsync("bucket", stream, "text/plain", "object")
    .Match(
        onSuccess: _ => "Upload successful",
        onFailure: (errorType, _) => errorType switch
        {
            MinioErrorType.Authorization => "Check your credentials",
            MinioErrorType.BucketNotFound => "Bucket doesn't exist",
            MinioErrorType.Connection => "Network issues",
            _ => "Something went wrong"
        }
    );

// Reshape without unwrapping — failures pass straight through
var sizes = await minioClient.StatObjectAsync("bucket", "object")
    .Map(stat => stat.Size);

// Log a failure and keep the result
var stat = (await minioClient.StatObjectAsync("bucket", "object"))
    .OnFailure((errorType, message) => _logger.LogWarning("Stat failed: {Type} {Message}", errorType, message));
```

### Available Operations (That Actually Return Useful Results):

| Operation | Returns | When to Use |
|-----------|---------|-------------|
| `PutObjectAsync` | `MinioResult<PutObjectResponse>` | Uploading files with proper error info |
| `PutStreamAsync` | `MinioResult<PutObjectResponse>` | Streaming uploads with auto-naming |
| `GetObjectAsync` | `MinioResult<ObjectStat>` | Getting object metadata + data |
| `DownloadObjectAsync` | `MinioResult<Stream>` | Downloading to MemoryStream |
| `DownloadObjectAsync` (with stream) | `MinioResult<ObjectStat>` | Downloading to existing stream |
| `DownloadObjectWithOffsetAndLengthAsync` | `MinioResult<ObjectStat>` | Ranged reads (64-bit offsets) |
| `RemoveObjectAsync` | `MinioResult` | Deleting objects (no return data) |
| `StatObjectAsync` | `MinioResult<ObjectStat>` | Checking if object exists |

### A Few Things Worth Knowing

- **Cancellation is not an error.** Cancel the token and you get an `OperationCanceledException`, not a
  `MinioResult` claiming `UnexpectedError`. Everything else comes back as a result.
- **Uploading a non-seekable stream?** Pass `objectSize:` explicitly — an HTTP request body has no `Length`.
- **`appendExtension:` is opt-in** and never doubles an extension you already wrote: `report.txt` stays
  `report.txt`.
- **Clients are meant to be long-lived.** Resolve one per configuration at startup. If you do create them
  repeatedly, the factory shares a connection pool so you won't run out of sockets — unless you supply your
  own `HttpClient` or proxy, in which case your transport is left strictly alone.

### Error Types You Can Actually Handle:

- `Authorization` - Your credentials are lying to you
- `BucketNotFound` - The bucket is in another castle
- `ObjectNotFound` - The object joined the witness protection program
- `Connection` - The server is ignoring your calls
- `Timeout` - The server is taking a nap
- `InvalidBucketName` - You used emojis in the bucket name, didn't you?
- ...and many more!

## What Could Possibly Go Wrong? (Spoiler: Everything, but now you can handle it!)

```csharp
var result = await minioClient.SomeOperation();
if (!result.IsSuccess)
{
    switch (result.ErrorType)
    {
        case MinioErrorType.Authorization:
            await _authService.RefreshToken();
            break;
        case MinioErrorType.BucketNotFound:
            await _notificationService.AlertMissingBucket();
            break;
        case MinioErrorType.Connection:
            await _retryService.RetryWithBackoff();
            break;
        default:
            _logger.LogError("Specific error: {ErrorType}", result.ErrorType);
            break;
    }
}
```

## Documentation (That You Might Actually Read Now)

- [.NET Client API Reference](https://min.io/docs/minio/linux/developers/dotnet/API.html) - The manual you'll open before everything breaks
- [.NET Quickstart Guide](https://min.io/docs/minio/linux/developers/dotnet/minio-dotnet.html) - "Quick" being slightly less relative now

## Source Code (For the Brave & Curious)

- [The original source](https://github.com/appany/Minio.AspNetCore/tree/main) - Where the magic (and hopefully fewer bugs) happen
- [MimeTypeMap](https://github.com/samuelneff/MimeTypeMap) - Because guessing file types is still hard.
  Vendored as an **internal** type, so it won't collide with the `MimeTypes` package in your own project.

## Running the Tests

```bash
dotnet test
```

One command, no flags, no setup beyond a running Docker daemon.

Almost everything runs against a **real Minio server**, started and disposed by
[Testcontainers](https://dotnet.testcontainers.org/). Uploads are verified by reading the object back rather
than by inspecting the request that was sent, so a test passes only when the bytes really round-trip. Failure
modes are provoked for real too: a missing bucket, rejected credentials, a refused connection, an expired
request timeout. The whole suite finishes in a few seconds.

Exactly one assertion uses a stubbed transport, because no server can supply it: that a 64-bit range offset
survives into the `Range` header.

That split is not academic. While the tests were stubbed, `Connection` and `Timeout` failures were silently
reported as `UnexpectedError` — simulated responses could not reveal it, and a real socket did so immediately.

## Known limitation: ranged reads

`DownloadObjectWithOffsetAndLengthAsync` cannot succeed with the Minio **client** 7.0.0 (the current release),
which turns every HTTP 206 response into a `PartialContentException`. This is upstream, not in this wrapper —
the raw Minio client fails the same way for `WithOffsetAndLength`, `WithLength`, a hand-written `Range`
header, and the file-based download path, while the identical request without a range succeeds.

Upgrading the **server** does not help: the failure reproduces identically against server releases
`2023-01-31` and `2025-09-07`. The fix has to come from a new client release. The method and its end-to-end
test are kept in place (the test is skipped with this reason) so both light up when one ships.

## License

Snail.Toolkit.Minio is a free and open source project, released under the permissible [MIT license](LICENSE).
