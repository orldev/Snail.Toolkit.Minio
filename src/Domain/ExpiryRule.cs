namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// One rule by which the server removes objects that have sat long enough.
/// </summary>
/// <remarks>
/// <para>
/// Expiry is the server's own housekeeping, and it is the only thing that clears what a request abandoned:
/// an object staged for an answer that never came outlives every process that knew about it.
/// </para>
/// <para>
/// Whole days is the granularity the protocol offers. A <see cref="TimeSpan"/> here would let a caller ask
/// for six hours and be quietly given one day.
/// </para>
/// </remarks>
public sealed record ExpiryRule
{
    /// <summary>Gets the name the rule is known by on the server.</summary>
    /// <remarks>Rules are replaced by name, so a stable one is what lets a rule be changed rather than doubled.</remarks>
    public required string Id { get; init; }

    /// <summary>Gets how many days an object lives before the server removes it.</summary>
    public required int Days { get; init; }

    /// <summary>Gets the prefix the rule applies to.</summary>
    /// <remarks>Empty applies it to the whole bucket, which is rarely what anyone means.</remarks>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the server acts on the rule.</summary>
    /// <remarks>A rule turned off stays on the server, so it can be brought back without being rewritten.</remarks>
    public bool IsEnabled { get; init; } = true;
}
