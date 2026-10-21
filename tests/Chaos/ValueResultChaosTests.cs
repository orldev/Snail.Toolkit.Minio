using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// A failed <see cref="StorageResult{T}"/> whose <c>T</c> is a value type.
/// </summary>
/// <remarks>
/// <see cref="StorageResult{T}"/> decides success by asking whether its value is null. A <c>bool</c> is never null:
/// <c>T?</c> on an unconstrained <c>T</c> is <c>T</c> itself for a struct, so a failed <c>StorageResult&lt;bool&gt;</c>
/// — what every <c>ExistsAsync</c> answers — reads as a success carrying <see langword="false"/>. The suite tests
/// results of <c>string</c> only, where the hole does not show.
/// </remarks>
public class ValueResultChaosTests
{
    private static readonly StorageError Down = new(StorageErrorKind.Connection, "the server is down");

    /// <summary>Breaks the one question a caller asks of a result: a failure has to answer that it has no value.</summary>
    [Fact]
    public void TryGetValue_OfAFailedValueTypeResult_AnswersFalse()
    {
        var failed = StorageResult<bool>.Failure(Down);

        Assert.False(failed.TryGetValue(out _));
    }

    /// <summary>Breaks the documented contract: reading the value of a failure throws, it does not hand back a default.</summary>
    [Fact]
    public void Value_OfAFailedValueTypeResult_Throws()
        => Assert.Throws<InvalidOperationException>(() => StorageResult<bool>.Failure(Down).Value);

    /// <summary>Breaks composition: a failure routed into the success branch is a lie the caller cannot detect.</summary>
    [Fact]
    public void Match_OnAFailedValueTypeResult_RunsTheFailureBranch()
    {
        var branch = StorageResult<bool>.Failure(Down).Match(onSuccess: _ => "success", onFailure: _ => "failure");

        Assert.Equal("failure", branch);
    }

    /// <summary>Breaks <c>Map</c> the same way: a failure must pass through, not be projected.</summary>
    [Fact]
    public void Map_OnAFailedValueTypeResult_PassesTheFailureThrough()
    {
        var mapped = StorageResult<long>.Failure(Down).Map(size => size + 1);

        Assert.False(mapped.IsSuccess);
    }

    /// <summary>
    /// The consequence inside the library itself: a server that cannot be reached while the bucket is being asked
    /// for makes <c>SetExpiryAsync</c> report that the bucket does not exist, which sends the operator to fix the
    /// wrong thing.
    /// </summary>
    [Fact]
    public async Task SetExpiry_WhenTheServerCannotBeReached_ReportsTheConnectionNotAMissingBucket()
    {
        var client = FakeClient.Answering(
            (nameof(IMinioClient.BucketExistsAsync), _ => throw new HttpRequestException("connection refused")));

        using var storage = FakeClient.Storage(client);

        var result = await storage.SetExpiryAsync("uploads", [new ExpiryRule { Id = "staged", Days = 1 }]);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.Connection, result.Error!.Kind);
    }
}
