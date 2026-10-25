using System;
using System.IO;
using System.Linq;
using Xunit;
using YDownloader.Downloads;

namespace YDownloader.Tests;

public class LinkExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ydl-export-" + Guid.NewGuid().ToString("N"));

    public LinkExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DownloadItem Item(string url, string? package = null) =>
        new(url, "x.bin", @"C:\Temp\none") { PackageName = package };

    [Fact]
    public void Text_has_one_link_per_line_without_duplicates_or_blanks()
    {
        string text = LinkExport.BuildText(new[]
        {
            "http://a.example.invalid/1", " http://a.example.invalid/2 ", "", "http://a.example.invalid/1", "   ",
        });

        Assert.Equal("http://a.example.invalid/1\r\nhttp://a.example.invalid/2\r\n", text);
    }

    [Fact]
    public void Empty_selection_gives_empty_text() => Assert.Equal("", LinkExport.BuildText(Array.Empty<string>()));

    [Fact]
    public void Name_is_the_package_when_all_items_share_one_else_links_txt()
    {
        Assert.Equal("Mein Film.txt", LinkExport.SuggestedName(new[] { Item("http://a.invalid/1", "Mein Film"), Item("http://a.invalid/2", "Mein Film") }));
        Assert.Equal("links.txt", LinkExport.SuggestedName(new[] { Item("http://a.invalid/1", "A"), Item("http://a.invalid/2", "B") }));
        Assert.Equal("links.txt", LinkExport.SuggestedName(new[] { Item("http://a.invalid/1", "A"), Item("http://a.invalid/2") }));
        Assert.Equal("links.txt", LinkExport.SuggestedName(Array.Empty<DownloadItem>()));
    }

    [Fact]
    public void Package_name_with_unsafe_characters_gives_a_valid_file_name()
    {
        string name = LinkExport.SuggestedName(new[] { Item("http://a.invalid/1", @"Film: Teil 2?") });

        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.EndsWith(".txt", name);
    }

    [Fact]
    public void Write_makes_a_utf8_file_without_bom_and_leaves_no_temp_file()
    {
        string path = Path.Combine(_dir, "links.txt");

        LinkExport.Write(path, new[] { "http://127.0.0.1:1/Bücher.rar", "http://127.0.0.1:1/b" });

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("http://127.0.0.1:1/Bücher.rar\r\nhttp://127.0.0.1:1/b\r\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void Write_replaces_an_existing_file_completely()
    {
        string path = Path.Combine(_dir, "links.txt");
        File.WriteAllText(path, new string('x', 5000));

        LinkExport.Write(path, new[] { "http://127.0.0.1:1/a" });

        Assert.Equal("http://127.0.0.1:1/a\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Write_to_a_missing_folder_fails_without_creating_anything()
    {
        string path = Path.Combine(_dir, "gibt-es-nicht", "links.txt");

        Assert.ThrowsAny<IOException>(() => LinkExport.Write(path, new[] { "http://127.0.0.1:1/a" }));
        Assert.Empty(Directory.GetDirectories(_dir));
    }
}
