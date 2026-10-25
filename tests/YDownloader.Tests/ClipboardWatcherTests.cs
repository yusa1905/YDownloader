using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YDownloader.Downloads;

namespace YDownloader.Tests;

/// <summary>Nur mit Attrappe: Die echte Zwischenablage wird nie angefasst. Alle Links sind ausgedachte <c>.invalid</c>-Adressen.</summary>
public class ClipboardWatcherTests
{
    private sealed class FakeClipboard : IClipboardReader
    {
        public string? Text;
        public int Reads;
        public string? ReadText() { Reads++; return Text; }
    }

    private static bool Host(Uri u) => u.Host.EndsWith("hoster.invalid", StringComparison.OrdinalIgnoreCase);
    private static bool Known(string name) => new[] { ".zip", ".mkv", ".pdf" }.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());

    private static ClipboardMatch? Scan(string? text) => ClipboardScan.Scan(text, Host, Known);

    [Fact]
    public void Debrid_host_links_match_even_without_file_extension()
    {
        var m = Scan("https://www.hoster.invalid/file/abc123");
        Assert.NotNull(m);
        Assert.Equal(1, m!.Count);
    }

    [Fact]
    public void Direct_links_with_known_extension_match()
    {
        Assert.NotNull(Scan("https://files.example.invalid/a/film.mkv"));
        Assert.NotNull(Scan("https://files.example.invalid/a/Doku.PDF"));
    }

    [Theory]
    [InlineData("https://files.example.invalid/a/Film.part1.rar")]
    [InlineData("https://files.example.invalid/a/Film.part02.rar")]
    [InlineData("https://files.example.invalid/a/Film.7z.001")]
    [InlineData("https://files.example.invalid/a/Film.z01")]
    public void Archive_parts_match(string link) => Assert.NotNull(Scan(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nur Text ohne Link")]
    [InlineData("https://news.example.invalid/artikel/12345")]
    [InlineData("https://www.example.invalid/")]
    [InlineData("ftp://files.example.invalid/a/film.mkv")]
    public void Everything_else_is_ignored(string? text) => Assert.Null(Scan(text));

    [Fact]
    public void Only_matching_links_go_into_the_hint()
    {
        var m = Scan("Seite: https://news.example.invalid/artikel/1\nhttps://files.example.invalid/x/a.zip\nhttps://www.hoster.invalid/f/9\n");
        Assert.NotNull(m);
        Assert.Equal(2, m!.Count);
        Assert.DoesNotContain("news.example", m.Text);
        Assert.Equal(new[] { "https://files.example.invalid/x/a.zip", "https://www.hoster.invalid/f/9" },
            m.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Huge_text_is_not_scanned()
    {
        string big = "https://files.example.invalid/a/film.mkv " + new string('x', ClipboardScan.MaxChars);
        Assert.Null(Scan(big));
    }

    [Fact]
    public void Number_of_links_is_capped()
    {
        string text = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"https://files.example.invalid/a/f{i}.zip"));
        Assert.Equal(ClipboardScan.MaxLinks, Scan(text)!.Count);
    }

    // ---------------------------------------------------------------- Watcher

    private static (ClipboardWatcher Watcher, FakeClipboard Clip, List<ClipboardMatch> Hints, Func<bool> SetActive) Make(bool active = false)
    {
        var clip = new FakeClipboard();
        var hints = new List<ClipboardMatch>();
        bool isActive = active;
        var w = new ClipboardWatcher(clip, Host, Known, () => isActive, hints.Add);
        return (w, clip, hints, () => isActive);
    }

    [Fact]
    public void A_matching_change_gives_one_hint()
    {
        var (w, clip, hints, _) = Make();
        clip.Text = "https://files.example.invalid/a/film.mkv";
        w.OnClipboardChanged();
        Assert.Single(hints);
        Assert.Equal(1, hints[0].Count);
    }

    [Fact]
    public void Same_content_is_not_reported_twice_but_again_after_something_else()
    {
        var (w, clip, hints, _) = Make();
        clip.Text = "https://files.example.invalid/a/film.mkv";
        w.OnClipboardChanged();
        w.OnClipboardChanged();
        Assert.Single(hints);

        clip.Text = "nur Text";
        w.OnClipboardChanged();
        clip.Text = "https://files.example.invalid/a/film.mkv";
        w.OnClipboardChanged();
        Assert.Equal(2, hints.Count);
    }

    [Fact]
    public void No_hint_and_no_read_while_the_window_is_active()
    {
        var (w, clip, hints, _) = Make(active: true);
        clip.Text = "https://files.example.invalid/a/film.mkv";
        w.OnClipboardChanged();
        Assert.Empty(hints);
        Assert.Equal(0, clip.Reads); // der Inhalt wird gar nicht erst gelesen
    }

    [Fact]
    public void Non_matching_content_gives_no_hint()
    {
        var (w, clip, hints, _) = Make();
        clip.Text = "https://news.example.invalid/artikel";
        w.OnClipboardChanged();
        clip.Text = null;
        w.OnClipboardChanged();
        Assert.Empty(hints);
    }
}
