using System;
using System.Text;
using Xunit;
using YDownloader.Engine;

namespace YDownloader.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("a:b.txt", "a_b.txt")]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("../x/y.zip", "y.zip")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData("CON .zip", "_CON .zip")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("name. . ", "name")]
    [InlineData("a\tb?.txt", "a_b_.txt")]
    [InlineData("Bücher 2024.pdf", "Bücher 2024.pdf")]
    [InlineData("  ", "download")]
    [InlineData("..", "download")]
    [InlineData(null, "download")]
    public void Sanitize_makes_valid_windows_names(string? input, string expected)
        => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Sanitize_shortens_long_names_and_keeps_the_extension()
    {
        string name = FileNames.Sanitize(new string('x', 300) + ".zip");

        Assert.Equal(FileNames.MaxLength, name.Length);
        Assert.EndsWith(".zip", name);
    }

    [Fact]
    public void Sanitize_does_not_split_a_surrogate_pair()
    {
        // 199 Zeichen, dann ein Emoji (zwei UTF-16-Einheiten) über die Grenze hinweg
        string name = FileNames.Sanitize(new string('x', 199) + "😀" + new string('y', 50));

        Assert.False(char.IsHighSurrogate(name[^1]));
        Assert.True(name.Length <= FileNames.MaxLength);
    }

    [Theory]
    [InlineData("attachment; filename=\"report 2024.pdf\"", "report 2024.pdf")]
    [InlineData("attachment; filename=My File.zip", "My File.zip")]
    [InlineData("attachment; filename*=UTF-8''%E2%82%AC%20rates.txt", "€ rates.txt")]
    [InlineData("attachment; filename=\"fallback.txt\"; filename*=UTF-8''echt%C3%A9.txt", "echté.txt")]
    [InlineData("attachment; filename*=iso-8859-1'de'B%FCcher.pdf", "Bücher.pdf")]
    [InlineData("attachment; filename=\"a \\\"quoted\\\" name.txt\"", "a \"quoted\" name.txt")]
    [InlineData("attachment; filename=\"BÃ¼cher.pdf\"", "Bücher.pdf")] // UTF-8 vom Server, als Latin-1 gelesen
    [InlineData("attachment; filename=\"Bücher.pdf\"", "Bücher.pdf")] // echtes Latin-1 bleibt
    [InlineData("inline", null)]
    [InlineData("attachment; size=10", null)]
    public void Content_disposition_is_read_leniently(string header, string? expected)
        => Assert.Equal(expected, FileNames.FromContentDisposition(header));

    [Fact]
    public void Content_disposition_with_mime_encoded_word()
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Bücher.pdf"));

        Assert.Equal("Bücher.pdf", FileNames.FromContentDisposition($"attachment; filename=\"=?UTF-8?B?{b64}?=\""));
        Assert.Equal("Ä b.txt", FileNames.FromContentDisposition("attachment; filename=\"=?UTF-8?Q?=C3=84_b.txt?=\""));
    }

    [Theory]
    [InlineData("https://host/files/report%202024.pdf?x=1", "report 2024.pdf")]
    [InlineData("https://host/", null)]
    public void Name_from_url(string url, string? expected) => Assert.Equal(expected, FileNames.FromUrl(new Uri(url)));
}
