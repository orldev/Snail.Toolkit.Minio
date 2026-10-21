using System.Collections.Frozen;
using System.Net.Http.Headers;
using Minio.DataModel;
using Snail.Toolkit.Minio.Domain;
using Snail.Toolkit.Minio.Media;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// Describes a stored object from what the server answered: a stat, a listing entry or the headers of a read.
/// </summary>
/// <remarks>
/// Three answers, one <see cref="StoredObject"/>: the names a caller wrote are kept and what the protocol
/// added is dropped, whichever path the answer arrived on.
/// </remarks>
internal static class ObjectDescriptions
{
    /// <summary>What the wire puts in front of a user's own metadata names.</summary>
    private const string MetadataPrefix = "x-amz-meta-";

    /// <summary>The names a server reports as metadata that no caller ever stored.</summary>
    private static readonly HashSet<string> ProtocolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Disposition", "Content-Language",
        "Cache-Control", "Expires", "ETag", "Last-Modified", "Accept-Ranges", "Date", "Server", "Connection"
    };

    /// <summary>
    /// Describes an object from what the SDK reported.
    /// </summary>
    /// <param name="bucket">The bucket read from.</param>
    /// <param name="name">The object read.</param>
    /// <param name="stat">What the server said.</param>
    /// <returns>The metadata a caller sees.</returns>
    internal static StoredObject Describe(string bucket, string name, ObjectStat stat) => new()
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
    internal static StoredObject Describe(string bucket, Item item) => new()
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
    internal static StoredObject Describe(string bucket, string name, HttpResponseMessage response)
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
}
