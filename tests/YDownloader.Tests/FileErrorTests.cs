using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using YDownloader.Downloads;
using YDownloader.Engine;
using Xunit;

namespace YDownloader.Tests;

/// <summary>Lokale Dateifehler (Ordner weg, Pfad zu lang, Platte abgezogen) sind keine abgebrochene Verbindung.</summary>
public class FileErrorTests
{
    private static string Text(Exception ex) => ErrorText.Describe(ex);

    private static IOException Win32(int code, string message = "x") => new(message, unchecked((int)(0x80070000u | (uint)code)));

    [Fact]
    public void Missing_folder_and_drive_get_their_own_text()
    {
        string expected = Loc.T("Error.FolderNotFound");
        Assert.Equal(expected, Text(new StorageException(new DirectoryNotFoundException())));
        Assert.Equal(expected, Text(new StorageException(new DriveNotFoundException())));
        Assert.Equal(expected, Text(new DirectoryNotFoundException()));
    }

    [Theory]
    [InlineData(0x03)] // Pfad nicht gefunden
    [InlineData(0x15)] // Gerät nicht bereit (Platte abgezogen)
    [InlineData(0x35)] // Netzwerkpfad nicht gefunden
    [InlineData(0x40)] // Netzwerkname nicht mehr verfügbar (Netzlaufwerk weg)
    [InlineData(0x43)]
    public void Windows_errors_for_a_vanished_target_are_not_a_network_abort(int code)
    {
        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(Win32(code)));
        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(new StorageException(Win32(code))));
    }

    [Fact]
    public void Path_too_long_gets_its_own_text()
    {
        string expected = Loc.T("Error.PathTooLong");
        Assert.Equal(expected, Text(new StorageException(new PathTooLongException())));
        Assert.Equal(expected, Text(new PathTooLongException()));
        Assert.Equal(expected, Text(Win32(0xCE)));
    }

    [Fact]
    public void Other_windows_file_errors_show_the_storage_text_with_the_reason()
    {
        string text = Text(Win32(0x20, "Die Datei wird von einem anderen Prozess verwendet"));

        Assert.Equal(Loc.T("Error.Storage", "Die Datei wird von einem anderen Prozess verwendet"), text);
        Assert.NotEqual(Loc.T("Error.ConnectionAborted"), text);
    }

    [Fact]
    public void Real_network_aborts_stay_a_connection_error()
    {
        string expected = Loc.T("Error.ConnectionAborted");
        Assert.Equal(expected, Text(new IOException("Verbindung vorzeitig beendet.")));
        Assert.Equal(expected, Text(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
    }

    [Fact]
    public void Access_denied_is_reported_as_no_access()
    {
        string expected = Loc.T("Error.NoAccess");
        Assert.Equal(expected, Text(new UnauthorizedAccessException()));
        Assert.Equal(expected, Text(new StorageException(new UnauthorizedAccessException())));
    }

    [Fact]
    public void Disk_full_keeps_its_text()
    {
        var full = new StorageException("voll", NativeFile.ErrorDiskFull, 5L * 1024 * 1024, 1L * 1024 * 1024);
        Assert.Equal(Loc.T("Error.DiskFull", 5, 1), Text(full));
    }

    // ---------------------------------------------------------------- Engine

    [Fact]
    public async Task Folder_that_cannot_be_created_is_a_storage_error_not_a_connection_error()
    {
        using var dir = new TempDir();
        string blocker = dir.File("blocker");
        File.WriteAllText(blocker, "ich bin eine Datei, kein Ordner");
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", Path.Combine(blocker, "sub", "a.bin")), null, default));

        Assert.NotEqual(Loc.T("Error.ConnectionAborted"), Text(ex));
        Assert.False(HttpDownloader.IsRetryable(ex));
    }

    [Fact]
    public async Task Missing_drive_is_a_storage_error()
    {
        // Ein Laufwerksbuchstabe, den es nicht gibt (ZZ: ist ungültig, Q: meist frei; beides endet als Speicherfehler)
        string drive = FreeDriveLetter() + @":\ydl-test\a.bin";
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", drive), null, default));

        Assert.Equal(Loc.T("Error.FolderNotFound"), Text(ex));
    }

    [Fact]
    public async Task Overlong_name_part_is_a_storage_error()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, new string('a', 300), "a.bin");
        using var dl = new HttpDownloader(new DownloadOptions());

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            dl.DownloadAsync(new DownloadRequest("http://127.0.0.1:1/a.bin", path), null, default));

        Assert.Equal(Loc.T("Error.PathTooLong"), Text(ex));
    }

    private static char FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives();
        for (char c = 'Z'; c >= 'D'; c--)
            if (Array.TrueForAll(used, d => char.ToUpperInvariant(d.Name[0]) != c)) return c;
        throw new InvalidOperationException("Kein freier Laufwerksbuchstabe");
    }
}
