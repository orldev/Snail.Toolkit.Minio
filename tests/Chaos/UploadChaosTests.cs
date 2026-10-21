using System.Runtime.CompilerServices;
using Minio.DataModel.Response;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Tests.Chaos;

/// <summary>
/// An upload described with metadata the wire accepts and the answer refuses.
/// </summary>
public class UploadChaosTests
{
    /// <summary>
    /// Breaks the contract that failures are data: two names that differ only in case pass the metadata check,
    /// are sent, the object is stored — and then describing what was stored throws from inside <c>PutAsync</c>.
    /// The caller gets an exception and a stored object it was never told about. The suite asserts this throw
    /// today (<c>MetadataMappingTests</c>); this test asserts the contract instead.
    /// </summary>
    [Fact]
    public async Task Put_WithTwoMetadataNamesDifferingOnlyInCase_AnswersInsteadOfThrowingAfterTheUpload()
    {
        var uploads = 0;

        var client = FakeClient.Answering((nameof(IMinioClient.PutObjectAsync), _ => Stored(ref uploads)));

        using var storage = FakeClient.Storage(client);
        using var content = new MemoryStream(new byte[4]);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["Author"] = "a", ["author"] = "b" };

        var thrown = await Record.ExceptionAsync(
            () => storage.PutAsync("bucket", content, new UploadOptions { Name = "a.png", Metadata = metadata }));

        Assert.Null(thrown);
        Assert.Equal(0, uploads);
    }

    private static Task<PutObjectResponse> Stored(ref int uploads)
    {
        uploads++;

        return Task.FromResult((PutObjectResponse)RuntimeHelpers.GetUninitializedObject(typeof(PutObjectResponse)));
    }
}
