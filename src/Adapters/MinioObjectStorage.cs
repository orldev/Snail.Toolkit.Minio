using System.Diagnostics;
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
public sealed class MinioObjectStorage : IObjectStorage, IDisposable
{
    /// <summary>The name of the activity source every operation reports under.</summary>
    public const string ActivitySourceName = "Snail.Toolkit.Minio";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private readonly string _name;
    private readonly IMinioClient _client;
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<MinioOptions> _options;
    private readonly ILogger _logger;

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

        using var activity = Start("put", bucket, name);

        var response = await MinioClientExtensions.ExecuteAsync(
            () => _client.PutObjectAsync(
                Describe(new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(name)
                    .WithContentType(mediaType)
                    .WithStreamData(content)
                    .WithObjectSize(size), options.Metadata),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!response.TryGetValue(out var stored))
            return Failed<StoredObject>("put", bucket, name, response.Error!, activity);

        return StorageResult<StoredObject>.Success(new StoredObject
        {
            Bucket = bucket,
            Name = name,
            Size = size,
            MediaType = mediaType,
            ETag = stored.Etag
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

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return StorageResult<ObjectContent>.Success(
            new ObjectContent(Describe(bucket, name, response), new ResponseStream(stream, response)));
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
    /// Repeats an operation while it fails for a reason that may not repeat.
    /// </summary>
    /// <typeparam name="T">What the operation produces.</typeparam>
    /// <param name="operation">The name to report under.</param>
    /// <param name="attempt">The work to repeat.</param>
    /// <param name="prepare">Puts the world back where the next attempt can start; false stops retrying.</param>
    /// <param name="cancellationToken">Cancels the wait between attempts.</param>
    /// <returns>The first successful outcome, or the last failure.</returns>
    private async Task<StorageResult<T>> RetryAsync<T>(
        string operation,
        Func<Task<StorageResult<T>>> attempt,
        Func<bool> prepare,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var settings = Settings;

        for (var attempted = 0; ; attempted++)
        {
            var result = await attempt().ConfigureAwait(false);

            if (result.IsSuccess || attempted >= settings.RetryAttempts || !IsWorthRetrying(result.Error) || !prepare())
                return result;

            var delay = settings.RetryDelay * Math.Pow(2, attempted);

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
    /// Decides whether a failure is worth another attempt.
    /// </summary>
    /// <param name="error">The failure to judge.</param>
    /// <returns><see langword="true"/> when the same call may yet succeed.</returns>
    private static bool IsWorthRetrying(StorageError error)
        => error.Kind is StorageErrorKind.Connection or StorageErrorKind.Timeout;

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

        _logger.LogWarning(
            "Minio {Operation} on {Bucket}/{Object} failed with {Kind}{Status}. {Message}",
            operation,
            bucket,
            name,
            error.Kind,
            error.StatusCode is { } status ? $" ({status})" : string.Empty,
            error.Message);
    }

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
        if (!MediaTypes.TryGetExtension(mediaType, out var extension)
            || name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return name;

        return name + extension;
    }

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
        LastModified = stat.LastModified
    };

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
            LastModified = content.LastModified
        };
    }

    /// <summary>
    /// A response body that closes its response when the caller is done reading.
    /// </summary>
    /// <param name="inner">The body being read.</param>
    /// <param name="response">The response the body belongs to.</param>
    /// <remarks>
    /// The caller is handed a stream and told it owns it. Without this, disposing that stream would leave
    /// the response — and the connection it is holding — alive until a garbage collection got to it.
    /// </remarks>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();

            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
