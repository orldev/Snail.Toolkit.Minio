using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// The result type's own promises.
/// </summary>
/// <remarks>
/// The defect this replaces: a successful result could carry <see langword="null"/>, and the readers
/// dereferenced the value anyway, so the failure surfaced inside the caller's own callback.
/// </remarks>
public class StorageResultTests
{
    private static readonly StorageError Error = new(StorageErrorKind.ObjectNotFound, "gone");

    [Fact]
    public void Success_CarriesItsValue()
    {
        var result = StorageResult<string>.Success("value");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void Success_RefusesAMissingValue()
        => Assert.Throws<ArgumentNullException>(() => StorageResult<string>.Success(null!));

    [Fact]
    public void Failure_CarriesItsError()
    {
        var result = StorageResult<string>.Failure(Error);

        Assert.False(result.IsSuccess);
        Assert.Equal(StorageErrorKind.ObjectNotFound, result.Error!.Kind);
    }

    [Fact]
    public void Value_OfAFailure_Throws()
        => Assert.Throws<InvalidOperationException>(() => StorageResult<string>.Failure(Error).Value);

    [Fact]
    public void TryGetValue_AnswersTheOutcome()
    {
        Assert.True(StorageResult<string>.Success("value").TryGetValue(out var value));
        Assert.Equal("value", value);

        Assert.False(StorageResult<string>.Failure(Error).TryGetValue(out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Match_RunsTheBranchThatApplies()
    {
        Assert.Equal("value", StorageResult<string>.Success("value").Match(v => v, e => e.Message));
        Assert.Equal("gone", StorageResult<string>.Failure(Error).Match(v => v, e => e.Message));
    }

    [Fact]
    public void Map_ReshapesASuccessAndPassesAFailureThrough()
    {
        Assert.Equal(5, StorageResult<string>.Success("value").Map(v => v.Length).Value);

        var failed = StorageResult<string>.Failure(Error).Map(v => v.Length);

        Assert.False(failed.IsSuccess);
        Assert.Same(Error, failed.Error);
    }

    [Fact]
    public void OnFailure_RunsOnlyForAFailureAndReturnsTheResult()
    {
        var seen = new List<StorageError>();

        var success = StorageResult<string>.Success("value").OnFailure(seen.Add);
        var failure = StorageResult<string>.Failure(Error).OnFailure(seen.Add);

        Assert.Equal("value", success.Value);
        Assert.False(failure.IsSuccess);
        Assert.Same(Error, Assert.Single(seen));
    }

    [Fact]
    public void ValuelessSuccess_HasNoError()
    {
        Assert.True(StorageResult.Success().IsSuccess);
        Assert.Null(StorageResult.Success().Error);
        Assert.False(StorageResult.Failure(Error).IsSuccess);
    }
}
