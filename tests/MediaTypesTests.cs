using Snail.Toolkit.Minio.Media;

namespace Snail.Toolkit.Minio.Tests;

/// <summary>
/// The media type table, which answers rather than throws.
/// </summary>
/// <remarks>
/// The defect this replaces: the lookup threw <see cref="ArgumentException"/> for a media type that started
/// with a dot even when asked not to throw for an unknown one, and compared strings using the current
/// culture, so the answer depended on the host's locale.
/// </remarks>
public class MediaTypesTests
{
    [Theory]
    [InlineData("report.txt", "text/plain")]
    [InlineData("REPORT.TXT", "text/plain")]
    [InlineData(".txt", "text/plain")]
    [InlineData("txt", "text/plain")]
    [InlineData("/var/data/report.txt?v=2", "text/plain")]
    public void MediaTypeFor_ReadsTheExtension(string fileName, string expected)
        => Assert.Equal(expected, MediaTypes.MediaTypeFor(fileName));

    [Fact]
    public void MediaTypeFor_FallsBackWhenTheExtensionIsUnknown()
        => Assert.Equal(MediaTypes.Default, MediaTypes.MediaTypeFor("report.made-up"));

    [Theory]
    [InlineData("text/plain", ".txt")]
    [InlineData("TEXT/PLAIN", ".txt")]
    [InlineData("image/png", ".png")]
    public void TryGetExtension_ReadsTheMediaType(string mediaType, string expected)
    {
        Assert.True(MediaTypes.TryGetExtension(mediaType, out var extension));
        Assert.Equal(expected, extension);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData("application/x-made-up")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryGetExtension_AnswersFalseInsteadOfThrowing(string? mediaType)
    {
        Assert.False(MediaTypes.TryGetExtension(mediaType, out var extension));
        Assert.Null(extension);
    }
}
