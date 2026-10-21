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

    /// <summary>
    /// Every shape of name resolves as it did when the extension was cut out as a new string and a bare
    /// name was given a dot: the old cut, looked up dotted, is the oracle.
    /// </summary>
    [Fact]
    public void TryGetMediaType_AnyName_ResolvesAsTheExtensionCutAsAString()
    {
        string[] names =
        [
            "", ".", "a.", "..", "?", "?x=1", ".?", "README", "readme.", "archive.tar.gz", "archive.TAR.GZ?v=1",
            "photo.jpeg?versionId=3", "/var/data/report.final.PDF", "dir.d/file", "text/plain", "image/png",
            ".AAC", "aac", "x.AddIn", new string('a', 300), new string('b', 255), "c." + new string('d', 300),
            "png", "PNG", "file.unknownextension", "\u00fc.txt", ".tar.gz", "a?b.png"
        ];

        foreach (var name in names)
        {
            var isKnown = MediaTypes.TryGetMediaType(name, out var mediaType);
            var wasKnown = MediaTypes.TryGetMediaType(ExtensionAsBefore(name), out var mediaTypeBefore);

            Assert.True(isKnown == wasKnown && mediaType == mediaTypeBefore, name);
        }
    }

    private static string ExtensionAsBefore(string fileName)
    {
        var query = fileName.IndexOf('?', StringComparison.Ordinal);

        if (query != -1)
            fileName = fileName[..query];

        if (fileName.StartsWith('.'))
            return fileName;

        var dot = fileName.LastIndexOf('.');

        return dot != -1 && fileName.Length > dot + 1 ? fileName[dot..] : "." + fileName;
    }
}
