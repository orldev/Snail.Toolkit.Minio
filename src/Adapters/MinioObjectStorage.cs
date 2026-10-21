using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio.DataModel.Args;
using Minio.DataModel.ILM;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Media;
using Snail.Toolkit.Minio.Ports;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Stores objects in Minio, or in anything else that speaks S3.
/// </summary>
/// <remarks>
/// <para>
/// With the default settings a listing asks for each object's user metadata, a MinIO extension: MinIO serves it,
/// and every other operation works the same against any S3 server. For AWS S3 or another S3 server, turn
/// <see cref="MinioOptions.CanListUserMetadata"/> off, and listings name objects without their metadata.
/// </para>
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

    private readonly string _name;
    private readonly IMinioClient _client;
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<MinioOptions> _options;
    private readonly ILogger _logger;
    private readonly Retrying _retrying;

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
        _retrying = new Retrying(name, options, logger);
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
            name = UploadRules.WithExtension(name, mediaType);

        if (!UploadRules.TryResolveSize(content, options.Size, out var size, out var sizeFault))
            return StorageResult<StoredObject>.Failure(sizeFault);

        if (UploadRules.Fault(options.Metadata) is { } metadataFault)
            return StorageResult<StoredObject>.Failure(metadataFault);

        if (UploadRules.Written(options.Metadata) is not { } written)
            return StorageResult<StoredObject>.Failure(new StorageError(
                StorageErrorKind.InvalidArgument,
                "Two metadata names differ only in case, and a header name has no case."));

        using var activity = Start("put", bucket, name);

        var response = await _retrying.RetryAsync(
            "put",
            (Client: _client, Bucket: bucket, Name: name, MediaType: mediaType, Content: content, Size: size,
                options.Metadata, CancellationToken: cancellationToken),
            static upload => MinioClientExtensions.ExecuteAsync(
                static upload => upload.Client.PutObjectAsync(
                    UploadRules.Describe(new PutObjectArgs()
                        .WithBucket(upload.Bucket)
                        .WithObject(upload.Name)
                        .WithContentType(upload.MediaType)
                        .WithStreamData(upload.Content)
                        .WithObjectSize(upload.Size), upload.Metadata),
                    upload.CancellationToken),
                upload,
                upload.CancellationToken),
            static _ => false,
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
            Metadata = written
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

        var opened = await _retrying.RetryAsync(
            "get",
            (Storage: this, Bucket: bucket, Name: name, CancellationToken: cancellationToken),
            static read => read.Storage.OpenAsync(read.Bucket, read.Name, range: null, read.CancellationToken),
            static _ => true,
            cancellationToken).ConfigureAwait(false);

        if (!opened.TryGetValue(out var response))
            return Failed<ObjectContent>("get", bucket, name, opened.Error!, activity);

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return StorageResult<ObjectContent>.Success(
                new ObjectContent(ObjectDescriptions.Describe(bucket, name, response), new ResponseStream(stream, response)));
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
        => DownloadAsync(bucket, name, destination, range: null, cancellationToken);

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

        return DownloadAsync(bucket, name, destination, (offset, length), cancellationToken);
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

        var stat = await _retrying.RetryAsync(
            "stat",
            (Client: _client, Bucket: bucket, Name: name, CancellationToken: cancellationToken),
            static stat => MinioClientExtensions.ExecuteAsync(
                static stat => stat.Client.StatObjectAsync(
                    new StatObjectArgs().WithBucket(stat.Bucket).WithObject(stat.Name),
                    stat.CancellationToken),
                stat,
                stat.CancellationToken),
            static _ => true,
            cancellationToken).ConfigureAwait(false);

        if (!stat.TryGetValue(out var described))
            return Failed<StoredObject>("stat", bucket, name, stat.Error!, activity);

        return StorageResult<StoredObject>.Success(ObjectDescriptions.Describe(bucket, name, described));
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

        var page = await _retrying.RetryAsync(
            "list",
            () => PageAsync(bucket, options, cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        return page.IsSuccess
            ? page
            : Failed<ObjectPage>("list", bucket, options.Prefix ?? string.Empty, page.Error!, activity);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The walk goes through the same funnel as every other read: the first page is asked under the circuit
    /// breaker and retried like a listing, and a failure further on is reported to the breaker and the log
    /// before it ends the walk. It used to go to the server once, outside both, while being the read this
    /// library recommends for a whole bucket.
    /// </remarks>
    public async IAsyncEnumerable<StorageResult<StoredObject>> EnumerateAsync(
        string bucket,
        ListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        options ??= new ListOptions();

        var prefix = options.Prefix ?? string.Empty;

        using var activity = Start("walk", bucket, prefix);

        var opened = await _retrying.RetryAsync(
            "walk",
            (Storage: this, Bucket: bucket, Options: options, CancellationToken: cancellationToken),
            static walk => walk.Storage.OpenWalkAsync(walk.Bucket, walk.Options, walk.CancellationToken),
            static _ => true,
            cancellationToken).ConfigureAwait(false);

        if (!opened.TryGetValue(out var walk))
        {
            yield return Failed<StoredObject>("walk", bucket, prefix, opened.Error, activity);

            yield break;
        }

        await using (walk.ConfigureAwait(false))
        {
            while (walk.Current is { } item)
            {
                if (!item.IsDir)
                    yield return StorageResult<StoredObject>.Success(ObjectDescriptions.Describe(bucket, item));

                var failure = await walk.AdvanceAsync(cancellationToken).ConfigureAwait(false);

                if (failure is null)
                    continue;

                _retrying.Noticed(failure);

                yield return Failed<StoredObject>("walk", bucket, prefix, failure, activity);

                yield break;
            }
        }
    }

    /// <summary>
    /// Starts a walk and advances it to its first entry.
    /// </summary>
    /// <param name="bucket">The bucket to walk.</param>
    /// <param name="options">What to list.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>The walk standing on its first entry, or on its end, or why the bucket could not be listed.</returns>
    /// <remarks>
    /// The first advance is where the first page is asked of the server, so it is the one attempt the retry
    /// funnel can repeat: a walk that failed there holds nothing a caller has seen.
    /// </remarks>
    private async Task<StorageResult<Walk>> OpenWalkAsync(
        string bucket,
        ListOptions options,
        CancellationToken cancellationToken)
    {
        var items = _client
            .ListObjectsEnumAsync(Listing(bucket, options, Settings.CanListUserMetadata), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var walk = new Walk(items);
        var failure = await walk.AdvanceAsync(cancellationToken).ConfigureAwait(false);

        if (failure is null)
            return StorageResult<Walk>.Success(walk);

        await walk.DisposeAsync().ConfigureAwait(false);

        return StorageResult<Walk>.Failure(failure);
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
    /// <remarks>
    /// Asking first and creating second is a race between callers, and the loser is answered 409 for a bucket
    /// that is exactly what it asked for. So a refused creation asks once more, and a bucket that is there by
    /// then is the success the contract promises; eight replicas provisioning at start-up used to fail seven times.
    /// </remarks>
    public async Task<StorageResult> CreateAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        var exists = await ExistsAsync(bucket, cancellationToken).ConfigureAwait(false);

        if (exists.TryGetValue(out var there) && there)
            return StorageResult.Success();

        using var activity = Start("bucket.create", bucket, string.Empty);

        var created = await _retrying.RetryAsync(
            "bucket.create",
            () => ValuelessAsync(() => _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(bucket), cancellationToken), cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (created.IsSuccess)
            return StorageResult.Success();

        var raced = await ExistsAsync(bucket, cancellationToken).ConfigureAwait(false);

        if (raced.TryGetValue(out var thereNow) && thereNow)
            return StorageResult.Success();

        return Answer("bucket.create", bucket, created, activity);
    }

    /// <inheritdoc />
    public async Task<StorageResult> RemoveAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        using var activity = Start("bucket.remove", bucket, string.Empty);

        var removed = await _retrying.RetryAsync(
            "bucket.remove",
            () => ValuelessAsync(() => _client.RemoveBucketAsync(
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

        var exists = await _retrying.RetryAsync(
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

        var removed = await _retrying.RetryAsync(
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
    public async Task<StorageResult> CopyAsync(
        string sourceBucket,
        string sourceName,
        string targetBucket,
        string targetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        using var activity = Start("copy", targetBucket, targetName);

        var copied = await _retrying.RetryAsync(
            "copy",
            () => ValuelessAsync(
                () => _client.CopyObjectAsync(
                    new CopyObjectArgs()
                        .WithBucket(targetBucket)
                        .WithObject(targetName)
                        .WithCopyObjectSource(new CopySourceObjectArgs()
                            .WithBucket(sourceBucket)
                            .WithObject(sourceName)),
                    cancellationToken),
                cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (copied.IsSuccess)
            return StorageResult.Success();

        Report("copy", targetBucket, targetName, copied.Error, activity);

        return StorageResult.Failure(copied.Error);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The bucket is asked for first because the server does not object to rules for one that is not
    /// there: measured against Minio 7.0.0 and server release 2025-09-07, writing a lifecycle to a
    /// misspelled bucket answers success and keeps nothing. A caller would learn of the typo only by
    /// noticing, months later, that nothing was ever cleaned up.
    /// </remarks>
    public async Task<StorageResult> SetExpiryAsync(
        string bucket,
        IReadOnlyList<ExpiryRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(rules);

        if (ExpiryRules.Fault(rules) is { } fault)
            return StorageResult.Failure(fault);

        var exists = await ExistsAsync(bucket, cancellationToken).ConfigureAwait(false);

        if (!exists.TryGetValue(out var there))
            return StorageResult.Failure(exists.Error);

        if (!there)
            return StorageResult.Failure(new StorageError(
                StorageErrorKind.BucketNotFound,
                $"The bucket '{bucket}' does not exist, so nothing would expire from it."));

        using var activity = Start("bucket.expiry.set", bucket, string.Empty);

        var written = await _retrying.RetryAsync(
            "bucket.expiry.set",
            () => ValuelessAsync(() => Write(bucket, rules, cancellationToken), cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        return Answer("bucket.expiry.set", bucket, written, activity);
    }

    /// <inheritdoc />
    public async Task<StorageResult<IReadOnlyList<ExpiryRule>>> GetExpiryAsync(
        string bucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        using var activity = Start("bucket.expiry.get", bucket, string.Empty);

        var read = await _retrying.RetryAsync(
            "bucket.expiry.get",
            () => MinioClientExtensions.ExecuteAsync(
                async () => await _client
                    .GetBucketLifecycleAsync(new GetBucketLifecycleArgs().WithBucket(bucket), cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (read.TryGetValue(out var configuration))
            return StorageResult<IReadOnlyList<ExpiryRule>>.Success(ExpiryRules.Expiries(configuration));

        if (ExpiryRules.IsUnset(read.Error))
            return StorageResult<IReadOnlyList<ExpiryRule>>.Success([]);

        return Failed<IReadOnlyList<ExpiryRule>>("bucket.expiry.get", bucket, string.Empty, read.Error, activity);
    }

    /// <summary>
    /// Writes the rules a bucket is to have, or clears them.
    /// </summary>
    /// <param name="bucket">The bucket to write to.</param>
    /// <param name="rules">The rules to write; none means clear.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The pending call.</returns>
    /// <remarks>
    /// Clearing goes through its own request because the protocol has no empty rule set: a configuration
    /// carrying no rules is refused, and the way to have none is to remove the configuration.
    /// </remarks>
    private Task Write(string bucket, IReadOnlyList<ExpiryRule> rules, CancellationToken cancellationToken)
        => rules.Count == 0
            ? _client.RemoveBucketLifecycleAsync(
                new RemoveBucketLifecycleArgs().WithBucket(bucket), cancellationToken)
            : _client.SetBucketLifecycleAsync(
                new SetBucketLifecycleArgs()
                    .WithBucket(bucket)
                    .WithLifecycleConfiguration(new LifecycleConfiguration([.. rules.Select(ExpiryRules.Rule)])),
                cancellationToken);

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
    /// partly written file is never left behind for a caller who was told the read failed. It is cut back to
    /// the length it had, never shorter: a stream positioned inside a file it already holds — parallel ranged
    /// reads into one preallocated file — used to lose everything after the position on a failure.
    /// </remarks>
    private async Task<StorageResult<StoredObject>> DownloadAsync(
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
        var length = destination.CanSeek ? destination.Length : -1;

        using var activity = Start(operation, bucket, name);

        var copied = await _retrying.RetryAsync(
            operation,
            (Storage: this, Bucket: bucket, Name: name, Destination: destination, Range: range, Start: start,
                Length: length, CancellationToken: cancellationToken),
            static copy => copy.Storage.CopyOnceAsync(copy.Bucket, copy.Name, copy.Destination, copy.Range, copy.CancellationToken),
            static copy => TryRewind(copy.Destination, copy.Start, copy.Length),
            cancellationToken).ConfigureAwait(false);

        if (copied.IsSuccess)
            return copied;

        TryRewind(destination, start, length);

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

            return StorageResult<StoredObject>.Success(ObjectDescriptions.Describe(bucket, name, response));
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
    /// Only the response headers are awaited here. The body of an object streams afterwards under the caller's
    /// token, so <see cref="MinioOptions.Timeout"/> bounds the request rather than the size of the object. The
    /// body of a failure is part of the request and is read under the same deadline: a server that answered a
    /// 503 and then stalled held a caller without a token of its own forever.
    /// </remarks>
    private async Task<StorageResult<HttpResponseMessage>> OpenAsync(
        string bucket,
        string name,
        (long Offset, long Length)? range,
        CancellationToken cancellationToken)
    {
        var settings = Settings;

        var signed = await MinioClientExtensions.ExecuteAsync(
            static read => read.Client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                .WithBucket(read.Bucket)
                .WithObject(read.Name)
                .WithExpiry((int)read.Lifetime.TotalSeconds)),
            (Client: _client, Bucket: bucket, Name: name, Lifetime: settings.SignedUrlLifetime),
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
                var failure = await MinioErrors.FromAsync(response, deadline.Token).ConfigureAwait(false);

                return StorageResult<HttpResponseMessage>.Failure(failure);
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
    /// <remarks>
    /// The cursor is a place in the order, not an object: everything up to and including its name is skipped.
    /// Skipping until the name itself was seen ended the listing on an empty page once the cursor object had
    /// been deleted between two pages, which is what a workload that lists and removes does. S3 orders keys by
    /// their UTF-8 bytes, and ordinal order agrees with it for everything but surrogate pairs.
    /// </remarks>
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
                .ListObjectsEnumAsync(Listing(bucket, options, Settings.CanListUserMetadata), cancellationToken)
                .ConfigureAwait(false))
            {
                if (skipping)
                {
                    skipping = string.CompareOrdinal(item.Key, options.Cursor) <= 0;

                    if (skipping)
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

                objects.Add(ObjectDescriptions.Describe(bucket, item));
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
    /// <param name="canListUserMetadata">Whether the server lists objects with their user metadata.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// User metadata is asked for where the server answers it, so a listing describes what it found rather than
    /// only naming it. A server that does not answer it would fail the whole listing inside the Minio client.
    /// </remarks>
    private static ListObjectsArgs Listing(string bucket, ListOptions options, bool canListUserMetadata)
    {
        var listing = new ListObjectsArgs()
            .WithBucket(bucket)
            .WithRecursive(options.IsRecursive)
            .WithIncludeUserMetadata(canListUserMetadata);

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
    private static async Task<StorageResult<bool>> ValuelessAsync(
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
    /// Returns a destination to where the read started, and to the length it had.
    /// </summary>
    /// <param name="destination">The stream to rewind.</param>
    /// <param name="start">The position the read began at, or -1 when the stream cannot seek.</param>
    /// <param name="length">The length the stream had, or -1 when it cannot seek.</param>
    /// <returns><see langword="true"/> when the stream is back where it started.</returns>
    /// <remarks>
    /// Only what the read appended is cut off. Bytes it overwrote inside the length the stream already had
    /// cannot be brought back, and are not pretended to be.
    /// </remarks>
    private static bool TryRewind(Stream destination, long start, long length)
    {
        if (start < 0 || !destination.CanSeek)
            return false;

        destination.Position = start;

        if (destination.Length > length)
            destination.SetLength(length);

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

        Log.Failed(
            _logger,
            IsExpected(error.Kind) ? LogLevel.Debug : LogLevel.Warning,
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
    /// <remarks>
    /// Nothing listening is asked first, so the activity's name is not built for every call that records nothing.
    /// </remarks>
    private Activity? Start(string operation, string bucket, string name)
    {
        if (!Source.HasListeners())
            return null;

        var activity = Source.StartActivity($"storage.{operation}", ActivityKind.Client);

        activity?.SetTag("storage.system", "minio");
        activity?.SetTag("storage.configuration", _name);
        activity?.SetTag("storage.bucket", bucket);
        activity?.SetTag("storage.object", name);

        return activity;
    }
}
