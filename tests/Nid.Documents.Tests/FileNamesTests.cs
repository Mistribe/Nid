using Nid.Documents.Domain;

namespace Nid.Documents.Tests;

public class FileNamesTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\x\\bulletin.pdf", "bulletin.pdf")]
    [InlineData("a\u202Eb\u0001c\"d.pdf", "abcd.pdf")]
    [InlineData("a\r\nb;c.pdf", "ab;c.pdf")]
    [InlineData(null, "document")]
    [InlineData("", "document")]
    [InlineData("   ", "document")]
    [InlineData("dir/", "document")]
    public void Clean(string? raw, string expected) => Assert.Equal(expected, FileNames.Clean(raw));

    [Fact]
    public void LongNameKeepsExtension()
    {
        var cleaned = FileNames.Clean(new string('a', 300) + ".pdf");
        Assert.Equal(255, cleaned.Length);
        Assert.EndsWith(".pdf", cleaned);
    }

    [Fact]
    public void LongNameDoesNotSplitSurrogatePair()
    {
        // 251 'a' + emoji (2 units) straddles the cut at 251 when ".pdf" is kept
        var cleaned = FileNames.Clean(new string('a', 250) + "\U0001F600\U0001F600.pdf");
        Assert.True(cleaned.Length <= 255);
        Assert.EndsWith(".pdf", cleaned);
        Assert.Equal(cleaned, new string(cleaned.Where((c, i) => !char.IsSurrogate(c)
            || (char.IsHighSurrogate(c) && i + 1 < cleaned.Length && char.IsLowSurrogate(cleaned[i + 1]))
            || (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(cleaned[i - 1]))).ToArray()));
    }

    [Fact]
    public void LoneSurrogatesAreDropped() =>
        Assert.Equal("ab\U0001F600.pdf", FileNames.Clean("a\uD800b\U0001F600\uDC00.pdf"));

    [Fact]
    public void Title() => Assert.Equal("Bulletin 09-2026", FileNames.TitleOf("Bulletin 09-2026.pdf"));
}
