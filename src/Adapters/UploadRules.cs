using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using Minio.DataModel.Args;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Media;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// What an upload has to satisfy before it is sent, and how it is described once it is.
/// </summary>
/// <remarks>
/// Every refusal here is a failure as data, found before a byte leaves the process: a size that cannot be
/// known, a metadata name that is not a header name, a value a header cannot carry, two names that differ
/// only in case.
/// </remarks>
internal static class UploadRules
{
    /// <summary>The characters RFC 7230 allows in a header name, a token.</summary>
    private static readonly SearchValues<char> HeaderNameCharacters =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

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
    internal static IReadOnlyDictionary<string, string>? Written(IReadOnlyDictionary<string, string>? metadata)
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
    internal static bool TryResolveSize(Stream content, long? stated, out long size, out StorageError fault)
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
    internal static string WithExtension(string name, string mediaType)
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
    internal static StorageError? Fault(IReadOnlyDictionary<string, string>? metadata)
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
    internal static PutObjectArgs Describe(PutObjectArgs args, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return args;

        var headers = metadata as IDictionary<string, string>
            ?? new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        return args.WithHeaders(headers);
    }
}
