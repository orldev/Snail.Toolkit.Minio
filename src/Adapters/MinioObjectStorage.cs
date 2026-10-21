using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio.DataModel;
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

    /// <summary>The longest this adapter waits between attempts, however the settings are written.</summary>
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    /// <summary>What the wire puts in front of a user's own metadata names.</summary>
    private const string MetadataPrefix = "x-amz-meta-";

    /// <summary>The code a server answers with when a bucket expires nothing.</summary>
    private const string NoLifecycleConfiguration = "NoSuchLifecycleConfiguration";

    /// <summary>The characters RFC 7230 allows in a header name, a token.</summary>
    private static readonly SearchValues<char> HeaderNameCharacters =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

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

        if (Written(options.Metadata) is not { } written)
            return StorageResult<StoredObject>.Failure(new StorageError(
                StorageErrorKind.InvalidArgument,
                "Two metadata names differ only in case, and a header name has no case."));

        using var activity = Start("put", bucket, name);

        var response = await RetryAsync(
            "put",
            (Client: _client, Bucket: bucket, Name: name, MediaType: mediaType, Content: content, Size: size,
                options.Metadata, CancellationToken: cancellationToken),
            static upload => MinioClientExtensions.ExecuteAsync(
                static upload => upload.Client.PutObjectAsync(
                    Describe(new PutObjectArgs()
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

    /// <summary>
    /// The metadata an upload will store, as the caller will read it back: names in any case.
    /// </summary>
    /// <param name="metadata">What the caller gave.</param>
    /// <returns>
    /// A copy nobody can write through, the shared empty one, or <see langword="null"/> when two names differ
    /// only in case.
    /// </returns>
    /// <remarks>
    /// Copied, not frozen: a handful of names is looked up a few times at most, and freezing costs more than
    /// those lookups save. Made before the upload, because it is also where two names that differ only in case
    /// are found: finding them after the upload threw out of a call whose object was already stored.
    /// </remarks>
    private static IReadOnlyDictionary<string, string>? Written(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return FrozenDictionary<string, string>.Empty;

        if (metadata.Count <= ObjectMetadata.MostNames)
            return Kept(metadata);

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in metadata)
        {
            if (!copy.TryAdd(key, value))
                return null;
        }

        return copy.AsReadOnly();
    }

    /// <summary>
    /// Copies an upload's few names, each once in any case.
    /// </summary>
    /// <param name="metadata">What the caller gave.</param>
    /// <returns>The copy, or <see langword="null"/> when two names differ only in case.</returns>
    private static ObjectMetadata? Kept(IReadOnlyDictionary<string, string> metadata)
    {
        var kept = new ObjectMetadata(metadata.Count);

        foreach (var (key, value) in metadata)
        {
            if (!kept.TryAdd(key, value))
                return null;
        }

        return kept;
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

        var stat = await RetryAsync(
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

        var opened = await RetryAsync(
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
                    yield return StorageResult<StoredObject>.Success(Describe(bucket, item));

                var failure = await walk.AdvanceAsync(cancellationToken).ConfigureAwait(false);

                if (failure is null)
                    continue;

                Noticed(failure);

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

    /// <summary>
    /// Tells the breaker about a failure met outside the funnel.
    /// </summary>
    /// <param name="failure">The failure met.</param>
    private void Noticed(StorageError failure)
    {
        if (IsWorthRetrying(failure))
            _breaker.Failed(Settings);
        else
            _breaker.Answered();
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

        var created = await RetryAsync(
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

        var removed = await RetryAsync(
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

        var copied = await RetryAsync(
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

        if (Fault(rules) is { } fault)
            return StorageResult.Failure(fault);

        var exists = await ExistsAsync(bucket, cancellationToken).ConfigureAwait(false);

        if (!exists.TryGetValue(out var there))
            return StorageResult.Failure(exists.Error);

        if (!there)
            return StorageResult.Failure(new StorageError(
                StorageErrorKind.BucketNotFound,
                $"The bucket '{bucket}' does not exist, so nothing would expire from it."));

        using var activity = Start("bucket.expiry.set", bucket, string.Empty);

        var written = await RetryAsync(
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

        var read = await RetryAsync(
            "bucket.expiry.get",
            () => MinioClientExtensions.ExecuteAsync(
                async () => await _client
                    .GetBucketLifecycleAsync(new GetBucketLifecycleArgs().WithBucket(bucket), cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken),
            () => true,
            cancellationToken).ConfigureAwait(false);

        if (read.TryGetValue(out var configuration))
            return StorageResult<IReadOnlyList<ExpiryRule>>.Success(Expiries(configuration));

        if (IsUnset(read.Error))
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
                    .WithLifecycleConfiguration(new LifecycleConfiguration([.. rules.Select(Rule)])),
                cancellationToken);

    /// <summary>
    /// Turns one rule into what the SDK sends.
    /// </summary>
    /// <param name="rule">The rule to send.</param>
    /// <returns>The same rule in the SDK's shape.</returns>
    private static LifecycleRule Rule(ExpiryRule rule) => new(
        null,
        rule.Id,
        new Expiration { Days = rule.Days },
        null,
        new RuleFilter(null, rule.Prefix, null),
        null,
        null,
        rule.IsEnabled ? LifecycleRule.LifecycleRuleStatusEnabled : LifecycleRule.LifecycleRuleStatusDisabled);

    /// <summary>
    /// Reads back the rules this library models, and no others.
    /// </summary>
    /// <param name="configuration">Everything the bucket carries.</param>
    /// <returns>The rules that expire objects after a number of days.</returns>
    /// <remarks>
    /// A bucket may also carry transitions, versioning rules and upload cleanups, which this library does
    /// not model. Reporting them as expiries would say an object dies on a day it does not.
    /// </remarks>
    private static IReadOnlyList<ExpiryRule> Expiries(LifecycleConfiguration configuration)
        => configuration.Rules is not { Count: > 0 } rules
            ? []
            : [.. rules
                .Where(rule => rule.Expiration?.Days is > 0)
                .Select(rule => new ExpiryRule
                {
                    Id = rule.ID ?? string.Empty,
                    Days = (int)rule.Expiration!.Days!.Value,
                    Prefix = rule.Filter?.Prefix ?? string.Empty,
                    IsEnabled = string.Equals(
                        rule.Status,
                        LifecycleRule.LifecycleRuleStatusEnabled,
                        StringComparison.OrdinalIgnoreCase)
                })];

    /// <summary>
    /// Decides whether a failure is the server saying the bucket expires nothing.
    /// </summary>
    /// <param name="error">The failure to judge.</param>
    /// <returns><see langword="true"/> when there is no configuration rather than no answer.</returns>
    /// <remarks>
    /// A bucket with no rules is a state, not a fault, and the protocol reports it as one: the request is
    /// refused with <c>NoSuchLifecycleConfiguration</c>. Handing that back as a failure would make every
    /// caller translate the same code before it could act on the answer. Judged by the code rather than by
    /// the message, which the server is free to reword.
    /// </remarks>
    private static bool IsUnset(StorageError? error)
        => string.Equals(error?.Code, NoLifecycleConfiguration, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the first thing wrong with a set of rules.
    /// </summary>
    /// <param name="rules">The rules to judge.</param>
    /// <returns>What is wrong, or <see langword="null"/> when they can be sent.</returns>
    /// <remarks>
    /// Checked here rather than at the server because the server's refusal names neither the rule nor the
    /// field, and a caller reading it cannot tell which of its rules was the bad one.
    /// </remarks>
    private static StorageError? Fault(IReadOnlyList<ExpiryRule> rules)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                return new StorageError(StorageErrorKind.InvalidArgument, "A rule has to be named.");

            if (!seen.Add(rule.Id))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The rule '{rule.Id}' is named twice, and the second would replace the first.");

            if (rule.Days <= 0)
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The rule '{rule.Id}' expires after {rule.Days} days, which is not a life.");
        }

        return null;
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

        var copied = await RetryAsync(
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
    /// this is also where the circuit breaker decides whether the server is worth asking at all. Every attempt
    /// tells the breaker how it ended — answered, failed, or abandoned by an exception — because a probe it
    /// never heard back from kept the gate shut for good.
    /// </remarks>
    private Task<StorageResult<T>> RetryAsync<T>(
        string operation,
        Func<Task<StorageResult<T>>> attempt,
        Func<bool> prepare,
        CancellationToken cancellationToken)
        where T : notnull
        => RetryAsync(
            operation,
            (Attempt: attempt, Prepare: prepare),
            static retry => retry.Attempt(),
            static retry => retry.Prepare(),
            cancellationToken);

    /// <summary>
    /// Repeats an operation on what it was handed while it fails for a reason that may not repeat.
    /// </summary>
    /// <typeparam name="TState">What every attempt is made with.</typeparam>
    /// <typeparam name="T">What the operation produces.</typeparam>
    /// <param name="operation">The name to report under.</param>
    /// <param name="state">What every attempt is made with.</param>
    /// <param name="attempt">The work to repeat, capturing nothing.</param>
    /// <param name="prepare">Puts the world back where the next attempt can start; false stops retrying.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>The first successful outcome, or the last failure.</returns>
    /// <remarks>
    /// The attempt is handed its state rather than capturing it, so the reads, the stat and the upload cost no
    /// closure and no delegate per call.
    /// </remarks>
    private async Task<StorageResult<T>> RetryAsync<TState, T>(
        string operation,
        TState state,
        Func<TState, Task<StorageResult<T>>> attempt,
        Func<TState, bool> prepare,
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
            StorageResult<T> result;

            try
            {
                result = await attempt(state).ConfigureAwait(false);
            }
            catch
            {
                _breaker.Abandoned();

                throw;
            }

            if (result.IsSuccess)
            {
                _breaker.Succeeded();

                return result;
            }

            var worthRetrying = IsWorthRetrying(result.Error);

            if (worthRetrying)
                _breaker.Failed(settings);
            else
                _breaker.Answered();

            if (attempted >= settings.RetryAttempts || !worthRetrying || !prepare(state))
                return result;

            if (!TryWait(settings, attempted, result.Error, out var delay))
                return result;

            Log.Retrying(_logger, operation, result.Error.Kind, delay, result.Error.Message);

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
            if (string.IsNullOrWhiteSpace(key) || key.AsSpan().ContainsAnyExcept(HeaderNameCharacters))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The metadata name '{key}' is not a header name.");

            if (value.AsSpan().ContainsAnyInRange('\u0000', '\u001F') || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F'))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The metadata value of '{key}' carries a control character.");

            if (!Ascii.IsValid(value))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The metadata value of '{key}' is not US-ASCII. Headers cannot carry anything else; "
                    + "encode the value yourself if it has to travel.");
        }

        return null;
    }

    /// <summary>
    /// Adds user metadata to an upload.
    /// </summary>
    /// <param name="args">The request being built.</param>
    /// <param name="metadata">The metadata to store, when there is any.</param>
    /// <returns>The same request.</returns>
    /// <remarks>
    /// The SDK reads the names it is given one by one, in order, and keeps none of them, so the caller's own
    /// dictionary is handed over whole; only one that is not an <see cref="IDictionary{TKey, TValue}"/> is copied.
    /// No names are not handed over at all: the SDK would rewrite the media type header it was given.
    /// </remarks>
    private static PutObjectArgs Describe(PutObjectArgs args, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return args;

        var headers = metadata as IDictionary<string, string>
            ?? new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        return args.WithHeaders(headers);
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
        LastModified = LocalMoment(stat.LastModified),
        Metadata = UserMetadata(stat.MetaData)
    };

    /// <summary>
    /// Reads the moment a stat reported on the clock the SDK read it with.
    /// </summary>
    /// <param name="reported">What the SDK parsed, or the first day of year one when the answer named no moment.</param>
    /// <returns>The moment on the local clock, or the first day of year one on UTC where the local clock cannot show it.</returns>
    /// <remarks>
    /// An answer without <c>Last-Modified</c> leaves the SDK's moment at its default. Taken on a clock east of UTC,
    /// that moment falls before year one and the conversion raises out of a stat that succeeded; there it reads as
    /// the same day on UTC, which is what a clock on UTC already shows and what any clock west of it shows on its own.
    /// Only the first day is asked about the local offset, so a stat that names its moment pays nothing more.
    /// </remarks>
    private static DateTimeOffset LocalMoment(DateTime reported)
    {
        var isFirstDay = reported.Kind != DateTimeKind.Utc && reported.Ticks < TimeSpan.TicksPerDay;

        if (isFirstDay && reported - DateTime.MinValue < TimeZoneInfo.Local.GetUtcOffset(reported))
            return DateTimeOffset.MinValue;

        return reported;
    }

    /// <summary>
    /// Keeps the metadata a caller wrote and drops what the protocol added.
    /// </summary>
    /// <param name="reported">Everything the SDK reported as metadata.</param>
    /// <returns>Only the names a caller would recognise.</returns>
    /// <remarks>
    /// The SDK mixes the media type and the transfer headers into the same dictionary as the user's own
    /// names, so handing it back whole would invent metadata nobody stored. What is left is a handful of
    /// names per object, kept where nobody can write through them: freezing so few costs more than every
    /// lookup it saves, once for each object a listing walks. The SDK reports a dictionary, read without
    /// boxing its enumerator; anything else, or more names than are compared in turn, keeps a dictionary.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> UserMetadata(IDictionary<string, string>? reported)
    {
        if (reported is null || reported.Count == 0)
            return FrozenDictionary<string, string>.Empty;

        if (reported.Count <= ObjectMetadata.MostNames
            && reported is Dictionary<string, string> dictionary
            && Kept(dictionary) is { } kept)
            return kept.Count == 0 ? FrozenDictionary<string, string>.Empty : kept;

        Dictionary<string, string>? metadata = null;

        foreach (var (key, value) in reported)
        {
            if (IsProtocol(key))
                continue;

            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata[key] = value;
        }

        return metadata is null ? FrozenDictionary<string, string>.Empty : metadata.AsReadOnly();
    }

    /// <summary>
    /// Keeps the names of a reported dictionary that the protocol did not add.
    /// </summary>
    /// <param name="reported">Everything the SDK reported as metadata.</param>
    /// <returns>The caller's names, or <see langword="null"/> when more came than were counted.</returns>
    private static ObjectMetadata? Kept(Dictionary<string, string> reported)
    {
        var kept = new ObjectMetadata(reported.Count);

        foreach (var (key, value) in reported)
        {
            if (!IsProtocol(key) && !kept.TrySet(key, value))
                return null;
        }

        return kept;
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
            Metadata = UserMetadata(response.Headers)
        };
    }

    /// <summary>
    /// Reads the metadata a caller wrote from the <c>x-amz-meta-</c> headers of a response.
    /// </summary>
    /// <param name="headers">The headers the server answered with.</param>
    /// <returns>The names without their prefix, each with its values joined as the headers join them.</returns>
    /// <remarks>
    /// The headers are read as they arrived: these values are opaque to HTTP, so parsing them into a
    /// validated copy, an array of strings per header, would change nothing but the cost. They are counted
    /// first, so a handful is kept in an array of exactly that many.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> UserMetadata(HttpResponseHeaders headers)
    {
        var count = 0;

        foreach (var (name, _) in headers.NonValidated)
        {
            if (name.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
                count++;
        }

        if (count == 0)
            return FrozenDictionary<string, string>.Empty;

        if (count <= ObjectMetadata.MostNames)
            return Kept(headers, count);

        Dictionary<string, string>? metadata = null;

        foreach (var (name, values) in headers.NonValidated)
        {
            if (!name.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata[name[MetadataPrefix.Length..]] = values.ToString();
        }

        return metadata is null ? FrozenDictionary<string, string>.Empty : metadata.AsReadOnly();
    }

    /// <summary>
    /// Keeps the <c>x-amz-meta-</c> headers of a response, counted beforehand.
    /// </summary>
    /// <param name="headers">The headers the server answered with.</param>
    /// <param name="count">How many of them carry the prefix.</param>
    /// <returns>The names without their prefix, each with its values joined as the headers join them.</returns>
    private static ObjectMetadata Kept(HttpResponseHeaders headers, int count)
    {
        var kept = new ObjectMetadata(count);

        foreach (var (name, values) in headers.NonValidated)
        {
            if (name.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
                kept.TrySet(name[MetadataPrefix.Length..], values.ToString());
        }

        return kept;
    }

    /// <summary>
    /// A listing being walked, one entry at a time, with its failures answered as data.
    /// </summary>
    /// <param name="items">The entries the SDK yields.</param>
    private sealed class Walk(IAsyncEnumerator<Item> items) : IAsyncDisposable
    {
        /// <summary>Gets the entry the walk stands on, or <see langword="null"/> once it has ended.</summary>
        public Item? Current { get; private set; }

        /// <summary>
        /// Moves to the next entry.
        /// </summary>
        /// <param name="cancellationToken">The caller's token, whose cancellation is rethrown rather than answered.</param>
        /// <returns><see langword="null"/> when the walk moved on or ended, or why it could not.</returns>
        public async ValueTask<StorageError?> AdvanceAsync(CancellationToken cancellationToken)
        {
            try
            {
                Current = await items.MoveNextAsync().ConfigureAwait(false) ? items.Current : null;

                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Current = null;

                return MinioErrors.From(exception);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => items.DisposeAsync();
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
