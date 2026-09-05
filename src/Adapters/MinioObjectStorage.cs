using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio.DataModel;
using Minio.DataModel.Args;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Media;
using Snail.Toolkit.Minio.Ports;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Stores objects in Minio, or in anything else that speaks S3.
/// </summary>
/// <remarks>
/// <para>
/// Writes and metadata go through the Minio SDK. Reads do not: they are issued against a URL this adapter
/// signs with the SDK and then fetches itself. Two reasons, both measured rather than assumed. Minio 7.0.0
/// turns every HTTP 206 answer into an exception, so ranged reads are impossible through the SDK's own read
/// path — verified against server releases 2023-01-31 and 2025-09-07, for <c>WithOffsetAndLength</c>,
/// <c>WithLength</c>, a hand-written <c>Range</c> header and the file-based path alike, with no bytes
/// delivered before the throw. And the SDK's read path hands over a callback rather than a stream, which
/// forces a whole object into memory before a caller sees its first byte.
/// </para>
/// <para>
/// The signed URL never leaves the process. It is valid for <see cref="MinioOptions.SignedUrlLifetime"/>,
/// long enough to reach the server standing next to it and no longer.
/// </para>
/// <para>
/// Reads and deletes are retried when the connection is lost or a request times out. An upload never is:
/// its stream may already be partly consumed, and replaying it would store the tail of a file as the whole
/// of one.
/// </para>
/// </remarks>
public sealed class MinioObjectStorage : IObjectStorage, IBuckets, IDisposable
{
    /// <summary>The name of the activity source every operation reports under.</summary>
    public const string ActivitySourceName = "Snail.Toolkit.Minio";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>The longest this adapter waits between attempts, however the settings are written.</summary>
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    /// <summary>What the wire puts in front of a user's own metadata names.</summary>
    private const string MetadataPrefix = "x-amz-meta-";

    /// <summary>The names a server reports as metadata that no caller ever stored.</summary>
    private static readonly HashSet<string> ProtocolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Disposition", "Content-Language",
        "Cache-Control", "Expires", "ETag", "Last-Modified", "Accept-Ranges", "Date", "Server", "Connection"
    };

    private readonly string _name;
    private readonly IMinioClient _client;
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<MinioOptions> _options;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _breaker = new();

    private bool _disposed;

    /// <summary>
    /// Binds the adapter to one named configuration.
    /// </summary>
    /// <param name="name">The configuration this instance serves.</param>
    /// <param name="client">The SDK client, owned by whoever registered it.</param>
    /// <param name="http">The transport reads are issued over, owned by this instance.</param>
    /// <param name="options">The named settings, re-read on every operation.</param>
    /// <param name="logger">Where failures and retries are reported.</param>
    public MinioObjectStorage(
        string name,
        IMinioClient client,
        HttpClient http,
        IOptionsMonitor<MinioOptions> options,
        ILogger<MinioObjectStorage> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _name = name;
        _client = client;
        _http = http;
        _options = options;
        _logger = logger;
    }

    private MinioOptions Settings => _options.Get(_name);

    /// <inheritdoc />
    public async Task<StorageResult<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(content);

        options ??= new UploadOptions();

        var name = options.Name ?? Guid.NewGuid().ToString("N");
        var mediaType = options.MediaType ?? MediaTypes.MediaTypeFor(name);

        if (options.AppendExtension)
            name = WithExtension(name, mediaType);

        if (!TryResolveSize(content, options.Size, out var size, out var sizeFault))
            return StorageResult<StoredObject>.Failure(sizeFault);

        if (Fault(options.Metadata) is { } metadataFault)
            return StorageResult<StoredObject>.Failure(metadataFault);

        using var activity = Start("put", bucket, name);

        var response = await RetryAsync(
            "put",
            () => MinioClientExtensions.ExecuteAsync(
                () => _client.PutObjectAsync(
                    Describe(new PutObjectArgs()
                        .WithBucket(bucket)
                        .WithObject(name)
                        .WithContentType(mediaType)
                        .WithStreamData(content)
                        .WithObjectSize(size), options.Metadata),
                    cancellationToken),
                cancellationToken),
            () => false,
            cancellationToken).ConfigureAwait(false);

        if (!response.TryGetValue(out var stored))
            return Failed<StoredObject>("put", bucket, name, response.Error!, activity);

        return StorageResult<StoredObject>.Success(new StoredObject
        {
            Bucket = bucket,
            Name = name,
            Size = size,
            MediaType = mediaType,
            ETag = stored.Etag,
            Metadata = options.Metadata is null
                ? FrozenDictionary<string, string>.Empty
                : options.Metadata.ToFrozenDictionary(
                    entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
        });
    }

    /// <inheritdoc />
    public async Task<StorageResult<ObjectContent>> GetAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var activity = Start("get", bucket, name);

        var opened = await RetryAsync(
            "get",
            () => OpenAsync(bucket, name, range: null, cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (!opened.TryGetValue(out var response))
            return Failed<ObjectContent>("get", bucket, name, opened.Error!, activity);

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return StorageResult<ObjectContent>.Success(
                new ObjectContent(Describe(bucket, name, response), new ResponseStream(stream, response)));
        }
        catch
        {
            response.Dispose();

            throw;
        }
    }

    /// <inheritdoc />
    public Task<StorageResult<StoredObject>> DownloadToAsync(
        string bucket,
        string name,
        Stream destination,
        CancellationToken cancellationToken = default)
        => CopyAsync(bucket, name, destination, range: null, cancellationToken);

    /// <inheritdoc />
    public Task<StorageResult<StoredObject>> DownloadRangeToAsync(
        string bucket,
        string name,
        Stream destination,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            return Task.FromResult(StorageResult<StoredObject>.Failure(
                new StorageError(StorageErrorKind.InvalidArgument, $"The offset cannot be negative, was {offset}.")));

        if (length <= 0)
            return Task.FromResult(StorageResult<StoredObject>.Failure(
                new StorageError(StorageErrorKind.InvalidArgument, $"The length has to be positive, was {length}.")));

        if (length - 1 > long.MaxValue - offset)
            return Task.FromResult(StorageResult<StoredObject>.Failure(
                new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"A range of {length} bytes from {offset} ends past the largest addressable byte.")));

        return CopyAsync(bucket, name, destination, (offset, length), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StorageResult<StoredObject>> StatAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var activity = Start("stat", bucket, name);

        var stat = await RetryAsync(
            "stat",
            () => _client.StatObjectAsync(bucket, name, cancellationToken: cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (!stat.TryGetValue(out var described))
            return Failed<StoredObject>("stat", bucket, name, stat.Error!, activity);

        return StorageResult<StoredObject>.Success(Describe(bucket, name, described));
    }

    /// <inheritdoc />
    public async Task<StorageResult<bool>> ExistsAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default)
    {
        var stat = await StatAsync(bucket, name, cancellationToken).ConfigureAwait(false);

        if (stat.IsSuccess)
            return StorageResult<bool>.Success(true);

        return stat.Error.Kind is StorageErrorKind.ObjectNotFound or StorageErrorKind.BucketNotFound
            ? StorageResult<bool>.Success(false)
            : StorageResult<bool>.Failure(stat.Error);
    }

    /// <inheritdoc />
    public async Task<StorageResult<ObjectPage>> ListAsync(
        string bucket,
        ListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        options ??= new ListOptions();

        if (options.PageSize <= 0)
            return StorageResult<ObjectPage>.Failure(new StorageError(
                StorageErrorKind.InvalidArgument,
                $"The page size has to be positive, was {options.PageSize}."));

        using var activity = Start("list", bucket, options.Prefix ?? string.Empty);

        var page = await RetryAsync(
            "list",
            () => PageAsync(bucket, options, cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        return page.IsSuccess
            ? page
            : Failed<ObjectPage>("list", bucket, options.Prefix ?? string.Empty, page.Error!, activity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StorageResult<StoredObject>> EnumerateAsync(
        string bucket,
        ListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        options ??= new ListOptions();

        await using var walk = _client
            .ListObjectsEnumAsync(Listing(bucket, options), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            Item? item = null;
            StorageError? failure = null;

            try
            {
                if (await walk.MoveNextAsync().ConfigureAwait(false))
                    item = walk.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failure = MinioErrors.From(exception);
            }

            if (failure is not null)
            {
                yield return StorageResult<StoredObject>.Failure(failure);

                yield break;
            }

            if (item is null)
                yield break;

            if (!item.IsDir)
                yield return StorageResult<StoredObject>.Success(Describe(bucket, item));
        }
    }

    /// <inheritdoc />
    public Task<StorageResult<Uri>> SignedUrlAsync(
        string bucket,
        string name,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
        => SignedAsync(
            bucket,
            name,
            lifetime,
            seconds => _client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                .WithBucket(bucket)
                .WithObject(name)
                .WithExpiry(seconds)),
            cancellationToken);

    /// <inheritdoc />
    public Task<StorageResult<Uri>> SignedUploadUrlAsync(
        string bucket,
        string name,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
        => SignedAsync(
            bucket,
            name,
            lifetime,
            seconds => _client.PresignedPutObjectAsync(new PresignedPutObjectArgs()
                .WithBucket(bucket)
                .WithObject(name)
                .WithExpiry(seconds)),
            cancellationToken);

    /// <inheritdoc />
    public async Task<StorageResult> CreateAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        var exists = await ExistsAsync(bucket, cancellationToken).ConfigureAwait(false);

        if (exists.TryGetValue(out var there) && there)
            return StorageResult.Success();

        using var activity = Start("bucket.create", bucket, string.Empty);

        var created = await RetryAsync(
            "bucket.create",
            () => Valueless(() => _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(bucket), cancellationToken), cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        return Answer("bucket.create", bucket, created, activity);
    }

    /// <inheritdoc />
    public async Task<StorageResult> RemoveAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        using var activity = Start("bucket.remove", bucket, string.Empty);

        var removed = await RetryAsync(
            "bucket.remove",
            () => Valueless(() => _client.RemoveBucketAsync(
                new RemoveBucketArgs().WithBucket(bucket), cancellationToken), cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        return Answer("bucket.remove", bucket, removed, activity);
    }

    /// <inheritdoc />
    public async Task<StorageResult<bool>> ExistsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        using var activity = Start("bucket.exists", bucket, string.Empty);

        var exists = await RetryAsync(
            "bucket.exists",
            () => MinioClientExtensions.ExecuteAsync(
                async () => await _client
                    .BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        return exists.IsSuccess
            ? exists
            : Failed<bool>("bucket.exists", bucket, string.Empty, exists.Error!, activity);
    }

    /// <inheritdoc />
    public async Task<StorageResult> RemoveAsync(
        string bucket,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var activity = Start("remove", bucket, name);

        var removed = await RetryAsync(
            "remove",
            async () =>
            {
                var attempt = await _client
                    .RemoveObjectAsync(bucket, name, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return attempt.IsSuccess
                    ? StorageResult<bool>.Success(true)
                    : StorageResult<bool>.Failure(attempt.Error);
            },
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (removed.IsSuccess)
            return StorageResult.Success();

        Report("remove", bucket, name, removed.Error, activity);

        return StorageResult.Failure(removed.Error);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the transport is released. The SDK client belongs to the container that built it, which may
    /// hand the same instance to code that is still using it.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>
    /// Copies a whole object, or one range of it, into a stream of the caller's.
    /// </summary>
    /// <param name="bucket">The bucket to read from.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="destination">Where to copy the bytes.</param>
    /// <param name="range">The range to read, or <see langword="null"/> for the whole object.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What was read, or why it could not be.</returns>
    /// <remarks>
    /// A destination that can seek is rewound to where it started before a retry and after a failure, so a
    /// partly written file is never left behind for a caller who was told the read failed.
    /// </remarks>
    private async Task<StorageResult<StoredObject>> CopyAsync(
        string bucket,
        string name,
        Stream destination,
        (long Offset, long Length)? range,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(destination);

        var operation = range is null ? "download" : "download.range";
        var start = destination.CanSeek ? destination.Position : -1;

        using var activity = Start(operation, bucket, name);

        var copied = await RetryAsync(
            operation,
            () => CopyOnceAsync(bucket, name, destination, range, cancellationToken),
            () => Rewind(destination, start),
            cancellationToken).ConfigureAwait(false);

        if (copied.IsSuccess)
            return copied;

        Rewind(destination, start);

        return Failed<StoredObject>(operation, bucket, name, copied.Error!, activity);
    }

    /// <summary>
    /// Performs one attempt at reading an object into a destination.
    /// </summary>
    /// <param name="bucket">The bucket to read from.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="destination">Where to copy the bytes.</param>
    /// <param name="range">The range to read, or <see langword="null"/> for the whole object.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What was read, or why it could not be.</returns>
    private async Task<StorageResult<StoredObject>> CopyOnceAsync(
        string bucket,
        string name,
        Stream destination,
        (long Offset, long Length)? range,
        CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(bucket, name, range, cancellationToken).ConfigureAwait(false);

        if (!opened.TryGetValue(out var response))
            return StorageResult<StoredObject>.Failure(opened.Error!);

        using (response)
        {
            try
            {
                await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return StorageResult<StoredObject>.Failure(MinioErrors.From(exception));
            }

            return StorageResult<StoredObject>.Success(Describe(bucket, name, response));
        }
    }

    /// <summary>
    /// Signs a read and asks the server for it.
    /// </summary>
    /// <param name="bucket">The bucket to read from.</param>
    /// <param name="name">The object to read.</param>
    /// <param name="range">The range to ask for, or <see langword="null"/> for the whole object.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answered response, whose body is still unread, or why the server refused.</returns>
    /// <remarks>
    /// Only the response headers are awaited here. The body streams afterwards under the caller's token,
    /// so <see cref="MinioOptions.Timeout"/> bounds the request rather than the size of the object.
    /// </remarks>
    private async Task<StorageResult<HttpResponseMessage>> OpenAsync(
        string bucket,
        string name,
        (long Offset, long Length)? range,
        CancellationToken cancellationToken)
    {
        var settings = Settings;

        var signed = await MinioClientExtensions.ExecuteAsync(
            () => _client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                .WithBucket(bucket)
                .WithObject(name)
                .WithExpiry((int)settings.SignedUrlLifetime.TotalSeconds)),
            cancellationToken).ConfigureAwait(false);

        if (!signed.TryGetValue(out var url))
            return StorageResult<HttpResponseMessage>.Failure(signed.Error!);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (range is { } wanted)
            request.Headers.Range = new RangeHeaderValue(wanted.Offset, wanted.Offset + wanted.Length - 1);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (settings.Timeout is { } timeout)
            deadline.CancelAfter(timeout);

        try
        {
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return StorageResult<HttpResponseMessage>.Success(response);

            using (response)
            {
                return StorageResult<HttpResponseMessage>.Failure(
                    await MinioErrors.FromAsync(response, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return StorageResult<HttpResponseMessage>.Failure(
                new StorageError(StorageErrorKind.Timeout, $"The request did not answer within {settings.Timeout}.")
                {
                    Cause = exception
                });
        }
        catch (Exception exception)
        {
            return StorageResult<HttpResponseMessage>.Failure(MinioErrors.From(exception));
        }
    }

    /// <summary>
    /// Reads one page of a listing.
    /// </summary>
    /// <param name="bucket">The bucket to list.</param>
    /// <param name="options">What to list, and how much of it.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page, or why the bucket could not be listed.</returns>
    private async Task<StorageResult<ObjectPage>> PageAsync(
        string bucket,
        ListOptions options,
        CancellationToken cancellationToken)
    {
        var objects = new List<StoredObject>();
        var prefixes = new List<string>();
        var skipping = options.Cursor is not null;

        string? cursor = null;

        try
        {
            await foreach (var item in _client
                .ListObjectsEnumAsync(Listing(bucket, options), cancellationToken)
                .ConfigureAwait(false))
            {
                if (skipping)
                {
                    skipping = !string.Equals(item.Key, options.Cursor, StringComparison.Ordinal);

                    continue;
                }

                if (item.IsDir)
                {
                    prefixes.Add(item.Key);

                    continue;
                }

                if (objects.Count == options.PageSize)
                {
                    cursor = objects[^1].Name;

                    break;
                }

                objects.Add(Describe(bucket, item));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StorageResult<ObjectPage>.Failure(MinioErrors.From(exception));
        }

        return StorageResult<ObjectPage>.Success(new ObjectPage(objects, cursor) { Prefixes = prefixes });
    }

    /// <summary>
    /// Builds the request one listing is read with.
    /// </summary>
    /// <param name="bucket">The bucket to list.</param>
    /// <param name="options">What to list.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// User metadata is asked for, so a listing describes what it found rather than only naming it.
    /// </remarks>
    private static ListObjectsArgs Listing(string bucket, ListOptions options)
    {
        var listing = new ListObjectsArgs()
            .WithBucket(bucket)
            .WithRecursive(options.IsRecursive)
            .WithIncludeUserMetadata(true);

        return options.Prefix is { } prefix ? listing.WithPrefix(prefix) : listing;
    }

    /// <summary>
    /// Signs a URL and checks that what came back is one.
    /// </summary>
    /// <param name="bucket">The bucket the object lives in.</param>
    /// <param name="name">The object the URL addresses.</param>
    /// <param name="lifetime">How long it stays valid, or the configured lifetime.</param>
    /// <param name="sign">Performs the signing for a number of seconds.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The URL, or why it could not be signed.</returns>
    private async Task<StorageResult<Uri>> SignedAsync(
        string bucket,
        string name,
        TimeSpan? lifetime,
        Func<int, Task<string>> sign,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var wanted = lifetime ?? Settings.SignedUrlLifetime;

        if (wanted < TimeSpan.FromSeconds(1) || wanted > TimeSpan.FromDays(7))
            return StorageResult<Uri>.Failure(new StorageError(
                StorageErrorKind.InvalidArgument,
                $"A signed URL lives between a second and seven days, not '{wanted}'."));

        var signed = await MinioClientExtensions
            .ExecuteAsync(() => sign((int)wanted.TotalSeconds), cancellationToken)
            .ConfigureAwait(false);

        if (!signed.TryGetValue(out var url))
            return StorageResult<Uri>.Failure(signed.Error!);

        return Uri.TryCreate(url, UriKind.Absolute, out var address)
            ? StorageResult<Uri>.Success(address)
            : StorageResult<Uri>.Failure(new StorageError(
                StorageErrorKind.Upstream,
                "The client signed something that is not an absolute URL."));
    }

    /// <summary>
    /// Runs an SDK call that produces nothing, in the shape the retry funnel takes.
    /// </summary>
    /// <param name="operation">The call to run.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>Success carrying nothing of interest, or the failure.</returns>
    private static async Task<StorageResult<bool>> Valueless(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var result = await MinioClientExtensions.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? StorageResult<bool>.Success(true) : StorageResult<bool>.Failure(result.Error);
    }

    /// <summary>
    /// Turns the funnel's answer back into one that carries no value.
    /// </summary>
    /// <param name="operation">The operation that ran.</param>
    /// <param name="bucket">The bucket it addressed.</param>
    /// <param name="result">What the funnel answered.</param>
    /// <param name="activity">The activity to mark on failure.</param>
    /// <returns>Success, or the failure, reported on the way.</returns>
    private StorageResult Answer(
        string operation,
        string bucket,
        StorageResult<bool> result,
        Activity? activity)
    {
        if (result.IsSuccess)
            return StorageResult.Success();

        Report(operation, bucket, string.Empty, result.Error, activity);

        return StorageResult.Failure(result.Error);
    }

    /// <summary>
    /// Repeats an operation while it fails for a reason that may not repeat.
    /// </summary>
    /// <typeparam name="T">What the operation produces.</typeparam>
    /// <param name="operation">The name to report under.</param>
    /// <param name="attempt">The work to repeat.</param>
    /// <param name="prepare">Puts the world back where the next attempt can start; false stops retrying.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>The first successful outcome, or the last failure.</returns>
    /// <remarks>
    /// Every call the adapter makes goes through here, including the ones that are never retried, because
    /// this is also where the circuit breaker decides whether the server is worth asking at all.
    /// </remarks>
    private async Task<StorageResult<T>> RetryAsync<T>(
        string operation,
        Func<Task<StorageResult<T>>> attempt,
        Func<bool> prepare,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var settings = Settings;

        if (!_breaker.TryEnter(settings, out var closedFor))
            return StorageResult<T>.Failure(new StorageError(
                StorageErrorKind.Upstream,
                $"Not attempted: {settings.CircuitBreakFailures} calls in a row failed, "
                + $"so this configuration is not being used for another {closedFor}.")
            {
                RetryAfter = closedFor
            });

        for (var attempted = 0; ; attempted++)
        {
            var result = await attempt().ConfigureAwait(false);

            if (result.IsSuccess)
            {
                _breaker.Succeeded();

                return result;
            }

            var worthRetrying = IsWorthRetrying(result.Error);

            if (worthRetrying)
                _breaker.Failed(settings);

            if (attempted >= settings.RetryAttempts || !worthRetrying || !prepare())
                return result;

            if (!TryWait(settings, attempted, result.Error, out var delay))
                return result;

            _logger.LogDebug(
                "Minio {Operation} failed with {Kind}, retrying in {Delay}. {Message}",
                operation,
                result.Error.Kind,
                delay,
                result.Error.Message);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Works out whether to wait, and for how long.
    /// </summary>
    /// <param name="settings">The settings the wait grows from.</param>
    /// <param name="attempted">How many attempts have already failed.</param>
    /// <param name="error">The failure that would be retried.</param>
    /// <param name="delay">How long to wait before the next attempt.</param>
    /// <returns><see langword="false"/> when the server asked for longer than is worth waiting.</returns>
    /// <remarks>
    /// A server that answers <c>Retry-After: 300</c> is saying it will not be ready inside any request's
    /// lifetime. Waiting that long inside a call nobody can see is worse than answering now and letting the
    /// caller decide.
    /// </remarks>
    private static bool TryWait(MinioOptions settings, int attempted, StorageError error, out TimeSpan delay)
    {
        delay = Backoff(settings, attempted);

        if (error.RetryAfter is not { } asked)
            return true;

        if (asked > MaximumBackoff)
            return false;

        if (asked > delay)
            delay = asked;

        return true;
    }

    /// <summary>
    /// Decides whether a failure is worth another attempt.
    /// </summary>
    /// <param name="error">The failure to judge.</param>
    /// <returns><see langword="true"/> when the same call may yet succeed.</returns>
    /// <remarks>
    /// A lost connection and a timeout, plus the statuses a server sends while it is briefly unable to
    /// answer. A rolling deployment answers 502 and 503 for a few seconds, and a client that gives up on
    /// those turns a deployment into an outage. The S3 codes are there because the SDK reports a 503 by
    /// code and without a status, and the same failure must not depend on which path it arrived by.
    /// </remarks>
    private static bool IsWorthRetrying(StorageError error)
        => error.Kind is StorageErrorKind.Connection or StorageErrorKind.Timeout
            || error.StatusCode is 408 or 429 or 500 or 502 or 503 or 504
            || error.Code is "SlowDown" or "InternalError" or "ServiceUnavailable" or "RequestTimeout" or "Busy";

    /// <summary>
    /// Works out how long to wait before the next attempt.
    /// </summary>
    /// <param name="settings">The settings the delay grows from.</param>
    /// <param name="attempted">How many attempts have already failed.</param>
    /// <returns>The wait, doubling per attempt and capped.</returns>
    /// <remarks>
    /// Capped because the doubling is otherwise unbounded: a generous delay and a generous attempt count
    /// multiply into a wait longer than the timer accepts, which raises out of a call that promised to
    /// answer.
    /// </remarks>
    private static TimeSpan Backoff(MinioOptions settings, int attempted)
    {
        var doublings = Math.Min(attempted, 16);
        var delay = settings.RetryDelay * Math.Pow(2, doublings);

        return delay < MaximumBackoff ? delay : MaximumBackoff;
    }

    /// <summary>
    /// Returns a destination to where the read started.
    /// </summary>
    /// <param name="destination">The stream to rewind.</param>
    /// <param name="start">The position the read began at, or -1 when the stream cannot seek.</param>
    /// <returns><see langword="true"/> when the stream is back where it started.</returns>
    private static bool Rewind(Stream destination, long start)
    {
        if (start < 0 || !destination.CanSeek)
            return false;

        destination.Position = start;

        if (destination.Length > start)
            destination.SetLength(start);

        return true;
    }

    /// <summary>
    /// Reports a failure to the log and the current activity, and hands it back as a result.
    /// </summary>
    /// <typeparam name="T">What the operation would have produced.</typeparam>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="bucket">The bucket it addressed.</param>
    /// <param name="name">The object it addressed.</param>
    /// <param name="error">Why it failed.</param>
    /// <param name="activity">The activity to mark, when one is being recorded.</param>
    /// <returns>A failed result carrying <paramref name="error"/>.</returns>
    private StorageResult<T> Failed<T>(
        string operation,
        string bucket,
        string name,
        StorageError error,
        Activity? activity)
        where T : notnull
    {
        Report(operation, bucket, name, error, activity);

        return StorageResult<T>.Failure(error);
    }

    /// <summary>
    /// Records a failure where an operator can find it.
    /// </summary>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="bucket">The bucket it addressed.</param>
    /// <param name="name">The object it addressed.</param>
    /// <param name="error">Why it failed.</param>
    /// <param name="activity">The activity to mark, when one is being recorded.</param>
    private void Report(string operation, string bucket, string name, StorageError error, Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error.Message);
        activity?.SetTag("storage.error", error.Kind.ToString());

        _logger.Log(
            IsExpected(error.Kind) ? LogLevel.Debug : LogLevel.Warning,
            "Minio {Operation} on {Bucket}/{Object} failed with {Kind}{Status}. {Message}",
            operation,
            bucket,
            name,
            error.Kind,
            error.StatusCode is { } status ? $" ({status})" : string.Empty,
            error.Message);
    }

    /// <summary>
    /// Decides whether a failure is part of ordinary work.
    /// </summary>
    /// <param name="kind">The failure to judge.</param>
    /// <returns><see langword="true"/> when an operator has no reason to look at it.</returns>
    /// <remarks>
    /// A missing object is an answer, not an incident. Logging it at warning turns a workload that checks
    /// whether things exist into a wall of alarms nobody reads.
    /// </remarks>
    private static bool IsExpected(StorageErrorKind kind)
        => kind is StorageErrorKind.ObjectNotFound
            or StorageErrorKind.BucketNotFound
            or StorageErrorKind.InvalidArgument
            or StorageErrorKind.InvalidObjectName
            or StorageErrorKind.InvalidBucketName
            or StorageErrorKind.NotSupported;

    /// <summary>
    /// Opens an activity for one operation.
    /// </summary>
    /// <param name="operation">What is being done.</param>
    /// <param name="bucket">The bucket addressed.</param>
    /// <param name="name">The object addressed.</param>
    /// <returns>The activity, or <see langword="null"/> when nothing is listening.</returns>
    private Activity? Start(string operation, string bucket, string name)
    {
        var activity = Source.StartActivity($"storage.{operation}", ActivityKind.Client);

        activity?.SetTag("storage.system", "minio");
        activity?.SetTag("storage.configuration", _name);
        activity?.SetTag("storage.bucket", bucket);
        activity?.SetTag("storage.object", name);

        return activity;
    }

    /// <summary>
    /// Works out how many bytes to upload.
    /// </summary>
    /// <param name="content">The stream to upload.</param>
    /// <param name="stated">The size the caller stated, when it stated one.</param>
    /// <param name="size">The size to send.</param>
    /// <param name="fault">Why the size could not be worked out.</param>
    /// <returns><see langword="true"/> when a size is known.</returns>
    /// <remarks>
    /// A stated size wins. Otherwise the stream's own length counts from its current position, so a
    /// partially read stream uploads what is left of it rather than what it once held.
    /// </remarks>
    private static bool TryResolveSize(Stream content, long? stated, out long size, out StorageError fault)
    {
        fault = null!;

        if (stated is { } given)
        {
            size = given;

            if (given >= 0)
                return true;

            fault = new StorageError(StorageErrorKind.InvalidArgument, $"The size cannot be negative, was {given}.");

            return false;
        }

        if (!content.CanSeek)
        {
            size = 0;
            fault = new StorageError(
                StorageErrorKind.NotSupported,
                $"The size of a stream that cannot seek has to be stated in '{nameof(UploadOptions.Size)}'.");

            return false;
        }

        size = content.Length - content.Position;

        return true;
    }

    /// <summary>
    /// Appends the extension a media type implies, unless the name already carries it.
    /// </summary>
    /// <param name="name">The object name.</param>
    /// <param name="mediaType">The media type to derive the extension from.</param>
    /// <returns>The name to store under.</returns>
    private static string WithExtension(string name, string mediaType)
    {
        if (string.Equals(mediaType, MediaTypes.Default, StringComparison.OrdinalIgnoreCase))
            return name;

        if (!MediaTypes.TryGetExtension(mediaType, out var extension)
            || name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return name;

        return name + extension;
    }

    /// <summary>
    /// Finds metadata that cannot be sent as headers.
    /// </summary>
    /// <param name="metadata">The metadata to judge.</param>
    /// <returns>What is wrong with it, or <see langword="null"/> when it can be sent.</returns>
    /// <remarks>
    /// A name outside the characters a header name allows, or a value carrying a carriage return, is the
    /// classic response-splitting payload. Refusing it here keeps it away from the SDK, which would either
    /// throw from somewhere the caller cannot see or, worse, send it.
    /// </remarks>
    private static StorageError? Fault(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return null;

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.All(IsHeaderName))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The metadata name '{key}' is not a header name.");

            if (value.Any(char.IsControl))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The metadata value of '{key}' carries a control character.");

            if (!value.All(char.IsAscii))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The metadata value of '{key}' is not US-ASCII. Headers cannot carry anything else; "
                    + "encode the value yourself if it has to travel.");
        }

        return null;
    }

    /// <summary>
    /// Decides whether a character may appear in a header name.
    /// </summary>
    /// <param name="candidate">The character to judge.</param>
    /// <returns><see langword="true"/> when RFC 7230 allows it in a token.</returns>
    private static bool IsHeaderName(char candidate)
        => char.IsAsciiLetterOrDigit(candidate) || "!#$%&\'*+-.^_`|~".Contains(candidate, StringComparison.Ordinal);

    /// <summary>
    /// Adds user metadata to an upload.
    /// </summary>
    /// <param name="args">The request being built.</param>
    /// <param name="metadata">The metadata to store, when there is any.</param>
    /// <returns>The same request.</returns>
    private static PutObjectArgs Describe(PutObjectArgs args, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return args;

        foreach (var (key, value) in metadata)
        {
            args = args.WithHeaders(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });
        }

        return args;
    }

    /// <summary>
    /// Describes an object from what the SDK reported.
    /// </summary>
    /// <param name="bucket">The bucket read from.</param>
    /// <param name="name">The object read.</param>
    /// <param name="stat">What the server said.</param>
    /// <returns>The metadata a caller sees.</returns>
    private static StoredObject Describe(string bucket, string name, ObjectStat stat) => new()
    {
        Bucket = bucket,
        Name = name,
        Size = stat.Size,
        MediaType = string.IsNullOrWhiteSpace(stat.ContentType) ? MediaTypes.Default : stat.ContentType,
        ETag = stat.ETag,
        LastModified = stat.LastModified,
        Metadata = UserMetadata(stat.MetaData)
    };

    /// <summary>
    /// Keeps the metadata a caller wrote and drops what the protocol added.
    /// </summary>
    /// <param name="reported">Everything the SDK reported as metadata.</param>
    /// <returns>Only the names a caller would recognise.</returns>
    /// <remarks>
    /// The SDK mixes the media type and the transfer headers into the same dictionary as the user's own
    /// names, so handing it back whole would invent metadata nobody stored.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> UserMetadata(IDictionary<string, string>? reported)
    {
        if (reported is null)
            return FrozenDictionary<string, string>.Empty;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in reported.Where(pair => !IsProtocol(pair.Key)))
        {
            metadata[key] = value;
        }

        return metadata.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides whether a name belongs to the protocol rather than to the caller.
    /// </summary>
    /// <param name="name">The name to judge.</param>
    /// <returns><see langword="true"/> when the wire, not the caller, put it there.</returns>
    private static bool IsProtocol(string name)
        => name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)
            || ProtocolNames.Contains(name);

    /// <summary>
    /// Describes an object from what a listing reported.
    /// </summary>
    /// <param name="bucket">The bucket listed.</param>
    /// <param name="item">One entry of the listing.</param>
    /// <returns>The metadata a caller sees.</returns>
    private static StoredObject Describe(string bucket, Item item) => new()
    {
        Bucket = bucket,
        Name = item.Key,
        Size = (long)item.Size,
        MediaType = string.IsNullOrWhiteSpace(item.ContentType) ? MediaTypes.Default : item.ContentType,
        ETag = item.ETag,
        LastModified = Moment(item.LastModifiedDateTime),
        Metadata = UserMetadata(item.UserMetadata)
    };

    /// <summary>
    /// Reads a moment a listing reported without assuming which clock it came from.
    /// </summary>
    /// <param name="reported">What the SDK parsed, when it parsed anything.</param>
    /// <returns>The moment, in UTC.</returns>
    private static DateTimeOffset? Moment(DateTime? reported)
    {
        if (reported is not { } moment)
            return null;

        return moment.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc))
            : new DateTimeOffset(moment.ToUniversalTime());
    }

    /// <summary>
    /// Describes an object from the headers the server answered with.
    /// </summary>
    /// <param name="bucket">The bucket read from.</param>
    /// <param name="name">The object read.</param>
    /// <param name="response">The response to read the headers of.</param>
    /// <returns>The metadata a caller sees.</returns>
    /// <remarks>
    /// <c>Content-Range</c> is preferred over <c>Content-Length</c> for the size, so a ranged read still
    /// reports how large the object is rather than how much of it was asked for.
    /// </remarks>
    private static StoredObject Describe(string bucket, string name, HttpResponseMessage response)
    {
        var content = response.Content.Headers;

        return new StoredObject
        {
            Bucket = bucket,
            Name = name,
            Size = content.ContentRange?.Length ?? content.ContentLength ?? 0,
            MediaType = content.ContentType?.MediaType ?? MediaTypes.Default,
            ETag = response.Headers.ETag?.Tag.Trim('"'),
            LastModified = content.LastModified,
            Metadata = response.Headers
                .Where(header => header.Key.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
                .ToFrozenDictionary(
                    header => header.Key[MetadataPrefix.Length..],
                    header => string.Join(", ", header.Value),
                    StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// A response body that closes its response when the caller is done reading.
    /// </summary>
    /// <param name="inner">The body being read.</param>
    /// <param name="response">The response the body belongs to.</param>
    /// <remarks>
    /// <para>
    /// The caller is handed a stream and told it owns it. Without this, disposing that stream would leave
    /// the response — and the connection it is holding — alive until a garbage collection got to it.
    /// </para>
    /// <para>
    /// Reading it after it is closed raises. The response body underneath answers zero bytes instead, which
    /// a caller cannot tell from an empty object — a truncated download that looks like a successful one.
    /// </para>
    /// </remarks>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        private bool _disposed;

        public override bool CanRead => !_disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;

                await inner.DisposeAsync().ConfigureAwait(false);
                response.Dispose();
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;

                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
