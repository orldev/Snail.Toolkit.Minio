using System.Net;
using Snail.Toolkit.Minio.Adapters;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// The translation table both paths of the adapter share.
/// </summary>
/// <remarks>
/// The coverage test is the point of this class: a kind added to <see cref="StorageErrorKind"/> without a
/// way of producing it is a promise the library cannot keep, and this fails until the sample exists.
/// </remarks>
public class ErrorKindTests
{
    private static readonly (StorageErrorKind Kind, Exception Thrown)[] Exceptions =
    [
        (StorageErrorKind.Connection, new HttpRequestException("refused")),
        (StorageErrorKind.Timeout, new TimeoutException()),
        (StorageErrorKind.NotSupported, new NotSupportedException()),
        (StorageErrorKind.ObjectDisposed, new ObjectDisposedException("stream")),
        (StorageErrorKind.InvalidArgument, new ArgumentException("bad")),
        (StorageErrorKind.Unexpected, new InvalidCastException("odd"))
    ];

    private static readonly (StorageErrorKind Kind, HttpStatusCode Status, string? Code)[] Answers =
    [
        (StorageErrorKind.BucketNotFound, HttpStatusCode.NotFound, "NoSuchBucket"),
        (StorageErrorKind.ObjectNotFound, HttpStatusCode.NotFound, "NoSuchKey"),
        (StorageErrorKind.AccessDenied, HttpStatusCode.Forbidden, "AccessDenied"),
        (StorageErrorKind.Authorization, HttpStatusCode.Unauthorized, null),
        (StorageErrorKind.InvalidBucketName, HttpStatusCode.BadRequest, "InvalidBucketName"),
        (StorageErrorKind.InvalidObjectName, HttpStatusCode.BadRequest, "KeyTooLongError"),
        (StorageErrorKind.InvalidArgument, HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange"),
        (StorageErrorKind.Upstream, HttpStatusCode.InternalServerError, null)
    ];

    /// <summary>
    /// Guards the rule rather than the cases: every kind has to be reachable from something.
    /// </summary>
    [Fact]
    public void EveryKind_CanBeProduced()
    {
        var produced = Exceptions.Select(sample => sample.Kind)
            .Concat(Answers.Select(sample => sample.Kind))
            .ToHashSet();

        Assert.Empty(Enum.GetValues<StorageErrorKind>().Except(produced));
    }

    [Fact]
    public void AnException_IsClassified()
    {
        foreach (var (kind, thrown) in Exceptions)
        {
            var error = MinioErrors.From(thrown);

            Assert.Equal(kind, error.Kind);
            Assert.Same(thrown, error.Cause);
        }
    }

    [Fact]
    public async Task AnAnswer_IsClassifiedByItsCodeAndStatus()
    {
        foreach (var (kind, status, code) in Answers)
        {
            using var response = Answer(status, code);

            var error = await MinioErrors.FromAsync(response, CancellationToken.None);

            Assert.Equal(kind, error.Kind);
            Assert.Equal((int)status, error.StatusCode);
            Assert.Equal(code, error.Code);
        }
    }

    [Fact]
    public async Task AnAnswerWithAnUnreadableBody_IsStillClassified()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not xml at all")
        };

        var error = await MinioErrors.FromAsync(response, CancellationToken.None);

        Assert.Equal(StorageErrorKind.ObjectNotFound, error.Kind);
        Assert.Null(error.Code);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string? code)
        => new(status)
        {
            Content = code is null
                ? new StringContent(string.Empty)
                : new StringContent($"<Error><Code>{code}</Code><Message>no</Message></Error>")
        };
}
