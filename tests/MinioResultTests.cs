using Toolkit.Minio.Entities;
using Toolkit.Minio.Extensions;

namespace Toolkit.Minio.Tests;

/// <summary>
/// Tests for <see cref="MinioResult"/> and the matching/projection extensions.
/// </summary>
public class MinioResultTests
{
    [Fact]
    public void TryGetValue_ReturnsValue_OnSuccess()
    {
        var result = MinioResult<string>.Success("payload");

        Assert.True(result.TryGetValue(out var value));
        Assert.Equal("payload", value);
    }

    [Fact]
    public void TryGetValue_ReturnsFalse_OnFailure()
    {
        var result = MinioResult<string>.Failure(MinioErrorType.ObjectNotFound, "missing");

        Assert.False(result.TryGetValue(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Match_RunsSuccessBranch()
    {
        var outcome = MinioResult<int>.Success(42).Match(
            onSuccess: value => $"ok:{value}",
            onFailure: (errorType, _) => $"err:{errorType}");

        Assert.Equal("ok:42", outcome);
    }

    [Fact]
    public void Match_RunsFailureBranch_WithErrorDetails()
    {
        var outcome = MinioResult<int>.Failure(MinioErrorType.AccessDenied, "nope").Match(
            onSuccess: value => $"ok:{value}",
            onFailure: (errorType, message) => $"err:{errorType}:{message}");

        Assert.Equal("err:AccessDenied:nope", outcome);
    }

    [Fact]
    public void Match_SubstitutesPlaceholder_WhenMessageIsNull()
    {
        var result = new MinioResult<int> { ErrorType = MinioErrorType.Connection };

        var outcome = result.Match(
            onSuccess: _ => "ok",
            onFailure: (_, message) => message);

        Assert.Equal("Unknown error", outcome);
    }

    [Fact]
    public async Task Match_OnTask_AvoidsIntermediateVariable()
    {
        var outcome = await Task.FromResult(MinioResult<int>.Success(7)).Match(
            onSuccess: value => value * 2,
            onFailure: (_, _) => -1);

        Assert.Equal(14, outcome);
    }

    [Fact]
    public async Task Match_OnNonGenericTask_RunsSuccessBranch()
    {
        var ran = false;

        await Task.FromResult(MinioResult.Success()).Match(
            onSuccess: () => ran = true,
            onFailure: (_, _) => ran = false);

        Assert.True(ran);
    }

    [Fact]
    public void Map_ProjectsValue_OnSuccess()
    {
        var mapped = MinioResult<int>.Success(21).Map(value => value * 2);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(42, mapped.Value);
    }

    [Fact]
    public void Map_PreservesError_OnFailure()
    {
        var mapped = MinioResult<int>.Failure(MinioErrorType.BucketNotFound, "gone")
            .Map(value => value.ToString());

        Assert.False(mapped.IsSuccess);
        Assert.Equal(MinioErrorType.BucketNotFound, mapped.ErrorType);
        Assert.Equal("gone", mapped.ErrorMessage);
    }

    [Fact]
    public void OnFailure_RunsOnlyForFailures_AndReturnsSameResult()
    {
        var observed = new List<MinioErrorType>();

        var success = MinioResult<int>.Success(1).OnFailure((errorType, _) => observed.Add(errorType));
        var failure = MinioResult<int>.Failure(MinioErrorType.Timeout, "slow")
            .OnFailure((errorType, _) => observed.Add(errorType));

        Assert.Equal([MinioErrorType.Timeout], observed);
        Assert.True(success.IsSuccess);
        Assert.Equal(MinioErrorType.Timeout, failure.ErrorType);
    }
}
