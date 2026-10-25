using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using YDownloader.Engine;
using Xunit;

namespace YDownloader.Tests;

/// <summary>Dateiname aus Antwort und Adressen: Content-Disposition, Abfrage, Weiterleitung, Content-Type, Webseite statt Datei.</summary>
public class FileNameGuessTests
{
    private static HttpResponseMessage Response(string? contentType = null, string? disposition = null)
    {
        var resp = new HttpResponseMessage { Content = new ByteArrayContent(Array.Empty<byte>()) };
        resp.Content.Headers.Remove("Content-Type");
        if (contentType is not null) resp.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (disposition is not null) resp.Content.Headers.TryAddWithoutValidation("Content-Disposition", disposition);
        return resp;
    }

    private static string Name(string finalUrl, string? original = null, string? type = null, string? disposition = null)
    {
        using var resp = Response(type, disposition);
        return FileNames.FromResponse(resp, new Uri(finalUrl), original is null ? null : new Uri(original));
    }

    // ---------------------------------------------------------------- (a) Content-Disposition

    [Fact]
    public void Content_disposition_wins_over_everything()
    {
        Assert.Equal("echt.zip", Name("http://h.invalid/download.php?file=falsch.rar", type: "application/pdf",
            disposition: "attachment; filename=\"echt.zip\""));
        Assert.Equal("echt.zip", Name("http://h.invalid/dl/8f3a9c", "http://h.invalid/movie.mkv", "video/mp4",
            "attachment; filename=echt.zip"));
    }

    [Fact]
    public void Empty_content_disposition_name_falls_back_to_the_urls()
    {
        Assert.Equal("a.zip", Name("http://h.invalid/a.zip", disposition: "attachment"));
    }

    // ---------------------------------------------------------------- (b) Abfrage

    [Theory]
    [InlineData("http://h.invalid/download.php?file=setup.zip", "setup.zip")]
    [InlineData("http://h.invalid/download.php?id=7&filename=My%20Tool.7z", "My Tool.7z")]
    [InlineData("http://h.invalid/get?name=ordner/pack.rar", "pack.rar")]
    [InlineData("http://h.invalid/get?FILE=a+b.pdf", "a b.pdf")]
    public void Query_parameter_with_an_extension_gives_the_name(string url, string expected) =>
        Assert.Equal(expected, Name(url));

    [Theory]
    [InlineData("http://h.invalid/download.php?file=setup")]      // keine Endung
    [InlineData("http://h.invalid/download.php?other=setup.zip")] // anderer Parameter
    public void Query_parameter_without_extension_or_name_is_ignored(string url) =>
        Assert.Equal("download.php", Name(url));

    [Fact]
    public void Real_path_name_beats_the_query()
    {
        Assert.Equal("a.zip", Name("http://h.invalid/a.zip?file=b.rar"));
    }

    // ---------------------------------------------------------------- (c) Original oder endgültige Adresse

    [Fact]
    public void Redirect_to_an_opaque_path_keeps_the_better_name_of_the_original_link()
    {
        Assert.Equal("movie.mkv", Name("http://cdn.invalid/dl/8f3a9c", "http://h.invalid/get/movie.mkv"));
    }

    [Fact]
    public void Final_name_wins_when_both_have_an_extension()
    {
        Assert.Equal("echt.mkv", Name("http://cdn.invalid/dl/echt.mkv", "http://h.invalid/get/movie.mp4"));
    }

    [Fact]
    public void Original_query_name_is_used_when_the_final_url_is_opaque()
    {
        Assert.Equal("setup.zip", Name("http://cdn.invalid/x/12345", "http://h.invalid/download.php?file=setup.zip"));
    }

    // ---------------------------------------------------------------- (d) Content-Type

    [Theory]
    [InlineData("application/zip", "zip")]
    [InlineData("application/x-zip-compressed", "zip")]
    [InlineData("application/x-7z-compressed", "7z")]
    [InlineData("application/vnd.rar", "rar")]
    [InlineData("application/x-rar-compressed", "rar")]
    [InlineData("application/pdf", "pdf")]
    [InlineData("video/mp4", "mp4")]
    [InlineData("video/x-matroska", "mkv")]
    [InlineData("audio/mpeg", "mp3")]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("APPLICATION/ZIP", "zip")]
    public void Content_type_table_gives_the_extension(string type, string ext) =>
        Assert.Equal("." + ext, FileNames.ExtensionFor(type));

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public void Generic_or_unknown_types_give_no_extension(string? type) => Assert.Null(FileNames.ExtensionFor(type));

    [Fact]
    public void Url_without_extension_takes_the_extension_from_the_content_type()
    {
        Assert.Equal("8f3a9c.zip", Name("http://h.invalid/dl/8f3a9c", type: "application/zip"));
        Assert.Equal("download.zip", Name("http://h.invalid/files/", type: "application/zip"));
        Assert.Equal("download.zip", Name("http://h.invalid/", type: "application/zip; charset=binary"));
    }

    [Fact]
    public void Page_extension_gives_way_to_the_content_type()
    {
        Assert.Equal("download.zip", Name("http://h.invalid/download.php?id=3", type: "application/zip"));
    }

    [Fact]
    public void Octet_stream_and_html_leave_the_name_alone()
    {
        Assert.Equal("8f3a9c", Name("http://h.invalid/dl/8f3a9c", type: "application/octet-stream"));
        Assert.Equal("download.php", Name("http://h.invalid/download.php", type: "text/html"));
        Assert.Equal("download", Name("http://h.invalid/"));
    }

    [Fact]
    public void A_real_extension_is_never_changed_by_the_content_type()
    {
        Assert.Equal("a.7z", Name("http://h.invalid/a.7z", type: "application/zip"));
    }

    // ---------------------------------------------------------------- Hilfen

    [Theory]
    [InlineData("setup.zip", true)]
    [InlineData("Movie.2020.mkv", true)]
    [InlineData("a.7z.001", true)]
    [InlineData("download.php", false)]
    [InlineData("page.html", false)]
    [InlineData("8f3a9c", false)]
    [InlineData("name.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("x.thisisfartoolong", false)]
    public void Real_extension(string? name, bool expected) => Assert.Equal(expected, FileNames.HasRealExtension(name));

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("A.RAR", true)]
    [InlineData("film.mkv", true)]
    [InlineData("song.mp3", true)]
    [InlineData("setup.exe", true)]
    [InlineData("pic.jpg", true)]
    [InlineData("page.html", false)]
    [InlineData("notes.txt", false)]
    [InlineData("8f3a9c", false)]
    public void Binary_names_expect_a_file_not_a_page(string name, bool expected) =>
        Assert.Equal(expected, FileNames.ExpectsBinary(name));

    [Fact]
    public void Guess_from_urls_without_any_extension_keeps_the_first_path_name()
    {
        Assert.Equal("8f3a9c", FileNames.GuessFromUrls(new Uri("http://h.invalid/dl/8f3a9c"), new Uri("http://h.invalid/x/other")));
        Assert.Null(FileNames.GuessFromUrls(new Uri("http://h.invalid/")));
    }

    // ---------------------------------------------------------------- (f) Webseite statt Datei (Engine)

    private static DownloadOptions Quick() => new()
    {
        Segments = 3,
        MinSegmentSize = 64 * 1024,
        RetryDelay = TimeSpan.FromMilliseconds(20),
    };

    [Theory]
    [InlineData("file.zip")]
    [InlineData("film.mkv")]
    [InlineData("setup.exe")]
    public async Task Html_answer_for_a_binary_name_fails_without_any_flag(string target)
    {
        using var server = new TestServer(50_000) { ContentType = "text/html; charset=utf-8" };
        using var dir = new TempDir();
        string path = dir.File(target);
        using var dl = new HttpDownloader(Quick());

        await Assert.ThrowsAsync<HtmlResponseException>(() => dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default));
        Assert.False(File.Exists(path));

        server.SupportsRanges = false; // Einzelstrom: dieselbe Regel
        await Assert.ThrowsAsync<HtmlResponseException>(() => dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("notes.txt")]
    [InlineData("noext")]
    public async Task Html_answer_for_a_page_or_unknown_name_still_downloads(string target)
    {
        using var server = new TestServer(50_000) { ContentType = "text/html" };
        using var dir = new TempDir();
        string path = dir.File(target);
        using var dl = new HttpDownloader(Quick());

        await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_binary_answer_for_a_binary_name_downloads_normally()
    {
        using var server = new TestServer(50_000) { ContentType = "application/zip" };
        using var dir = new TempDir();
        string path = dir.File("file.zip");
        using var dl = new HttpDownloader(Quick());

        await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("setup.zip", "http://h.invalid/download.php?file=setup.zip", true)] // Name aus der Abfrage geraten
    [InlineData("download.php", "http://h.invalid/download.php?file=setup.zip", true)]
    [InlineData("download", "http://h.invalid/files/", true)]
    [InlineData("echt.zip", "http://h.invalid/download.php?file=setup.zip", false)] // vom Server oder Nutzer
    public void Guessed_name_is_replaced_by_the_probe_at_start(string name, string url, bool guessed) =>
        Assert.Equal(guessed, Downloads.DownloadManager.NameIsGuessed(name, url));

    [Fact]
    public async Task Probe_prefers_content_disposition_and_follows_the_redirect_name()
    {
        using var server = new TestServer(50_000) { ContentDisposition = "attachment; filename=\"echt.zip\"" };
        using var dl = new HttpDownloader(Quick());

        var info = await dl.ProbeAsync(server.RedirectUrl, default);

        Assert.Equal("echt.zip", info.FileName);

        server.ContentDisposition = null;
        info = await dl.ProbeAsync(server.RedirectUrl, default); // /redirect ohne Endung → file.bin der endgültigen Adresse
        Assert.Equal("file.bin", info.FileName);
    }
}
