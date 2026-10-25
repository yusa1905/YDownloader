using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Xunit;
using YDownloader.Packages;

namespace YDownloader.Tests;

/// <summary>
/// Testarchive sind selbst erzeugt: ZIP mit .NET, 7z mit dem <c>tar.exe</c> von Windows. RAR lässt sich nicht erzeugen und 7-Zip
/// ist auf dem Rechner nicht vorhanden, deshalb bleiben der 7-Zip-Weg und RAR hier ungeprüft (nur die Listenauswertung ist getestet).
/// </summary>
public class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ydl-extract-" + Guid.NewGuid().ToString("N"));
    private static readonly string Tar = Path.Combine(Environment.SystemDirectory, "tar.exe");

    public ArchiveExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Dir(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private string MakeZip(string path, params (string Name, string Text)[] entries)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
            using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        return path;
    }

    private static ExtractJob Single(string archive, string target, ArchiveType type) =>
        new(archive, target, type, false, new[] { archive });

    private static ArchiveExtractor Extractor(string? sevenZip = null, string? tar = null, Func<string, long?>? space = null) =>
        new(() => sevenZip, () => tar, space ?? (_ => long.MaxValue));

    [Fact]
    public void Zip_is_extracted_with_dotnet()
    {
        string target = Dir("pkg");
        string zip = MakeZip(Path.Combine(_root, "a.zip"), ("hello.txt", "hi"), ("sub/dir/b.txt", "deep"));
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(2, r.Files);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(target, "hello.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(target, "sub", "dir", "b.txt")));
        Assert.Empty(Directory.GetDirectories(target, ".ydl-extract-*")); // Staging aufgeräumt
        Assert.True(File.Exists(zip)); // das Archiv bleibt
    }

    [Fact]
    public void Single_zip_goes_into_subfolder_named_after_archive()
    {
        string zip = MakeZip(Path.Combine(_root, "Film.zip"), ("a.txt", "A"), ("d/b.txt", "B"));
        string target = ExtractPlanner.SingleTargetDir(zip);
        Assert.Equal(Path.Combine(_root, "Film"), target);
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(target, "d", "b.txt")));
        Assert.True(File.Exists(zip));
        Assert.Equal(new[] { "Film" }, Directory.GetDirectories(_root).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Single_archive_extracted_twice_never_overwrites()
    {
        string zip = MakeZip(Path.Combine(_root, "Film.zip"), ("a.txt", "A"));
        string target = ExtractPlanner.SingleTargetDir(zip);
        Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        File.WriteAllText(Path.Combine(target, "a.txt"), "geändert");
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("geändert", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a (1).txt")));
    }

    [Fact]
    public void Failed_run_leaves_no_new_empty_target_folder()
    {
        string zip = MakeZip(Path.Combine(_root, "slip.zip"), ("../evil.txt", "evil"));
        string target = ExtractPlanner.SingleTargetDir(zip);
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractFailure.UnsafePath, r.Failure);
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void Existing_target_folder_is_kept_after_failure()
    {
        string zip = MakeZip(Path.Combine(_root, "slip.zip"), ("../evil.txt", "evil"));
        string target = ExtractPlanner.SingleTargetDir(zip);
        Directory.CreateDirectory(target);
        Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Single_7z_made_by_tar_goes_into_subfolder()
    {
        string src = Dir("src");
        File.WriteAllText(Path.Combine(src, "one.txt"), "eins");
        string archive = Path.Combine(_root, "Einzeln.7z");
        Assert.Equal(0, RunTar("-a", "-cf", archive, "-C", src, "one.txt"));
        string target = ExtractPlanner.SingleTargetDir(archive);
        var r = Extractor(tar: Tar).Run(Single(archive, target, ArchiveType.SevenZip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("eins", File.ReadAllText(Path.Combine(target, "one.txt")));
    }

    [Fact]
    public void Single_7z_without_any_tool_needs_sevenzip_and_makes_no_folder()
    {
        string archive = Path.Combine(_root, "Einzeln.7z");
        File.WriteAllBytes(archive, new byte[200]);
        string target = ExtractPlanner.SingleTargetDir(archive);
        var r = Extractor().Run(Single(archive, target, ArchiveType.SevenZip), CancellationToken.None);
        Assert.Equal(ExtractState.NeedSevenZip, r.State);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Existing_files_are_never_overwritten()
    {
        string target = Dir("pkg");
        File.WriteAllText(Path.Combine(target, "hello.txt"), "mine");
        string zip = MakeZip(Path.Combine(_root, "a.zip"), ("hello.txt", "new"));
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "hello.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "hello (1).txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("sub/../../evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("/evil.txt")]
    public void Zip_slip_is_refused_and_nothing_is_written(string entry)
    {
        string target = Dir("pkg");
        string zip = MakeZip(Path.Combine(_root, "slip.zip"), ("ok.txt", "fine"), (entry, "evil"));
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.UnsafePath, r.Failure);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Not_enough_space_stops_before_writing()
    {
        string target = Dir("pkg");
        string zip = MakeZip(Path.Combine(_root, "a.zip"), ("big.txt", new string('x', 1000)));
        var r = Extractor(space: _ => 1000).Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractFailure.NoSpace, r.Failure);
        Assert.StartsWith(SpaceCheck.Required(1000) + "|1000", r.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Encrypted_zip_without_sevenzip_needs_sevenzip()
    {
        string target = Dir("pkg");
        string zip = MakeZip(Path.Combine(_root, "enc.zip"), ("a.txt", "secret"));
        byte[] bytes = File.ReadAllBytes(zip);
        for (int i = 0; i < bytes.Length - 4; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2) bytes[i + 8] |= 1;
        File.WriteAllBytes(zip, bytes);
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.NeedSevenZip, r.State);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Multipart_without_sevenzip_needs_sevenzip_and_leaves_archives()
    {
        string target = Dir("pkg");
        string a = Path.Combine(target, "x.7z.001"), b = Path.Combine(target, "x.7z.002");
        File.WriteAllBytes(a, new byte[100]);
        File.WriteAllBytes(b, new byte[100]);
        var job = new ExtractJob(a, target, ArchiveType.SevenZip, true, new[] { a, b });
        var r = Extractor(tar: Tar).Run(job, CancellationToken.None); // tar darf mehrteilige Archive nicht anfassen
        Assert.Equal(ExtractState.NeedSevenZip, r.State);
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal(2, Directory.GetFileSystemEntries(target).Length);
    }

    [Fact]
    public void Missing_part_fails()
    {
        string target = Dir("pkg");
        string a = Path.Combine(target, "x.zip");
        MakeZip(a, ("a.txt", "a"));
        var job = new ExtractJob(a, target, ArchiveType.Zip, false, new[] { a, Path.Combine(target, "gone.z01") });
        Assert.Equal(ExtractFailure.MissingParts, Extractor().Run(job, CancellationToken.None).Failure);
    }

    [Fact]
    public void Corrupt_zip_fails_with_reason()
    {
        string target = Dir("pkg");
        string zip = Path.Combine(_root, "bad.zip");
        File.WriteAllBytes(zip, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        var r = Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.Unreadable, r.Failure);
    }

    [Fact]
    public void Cancel_throws_and_cleans_up()
    {
        string target = Dir("pkg");
        string zip = MakeZip(Path.Combine(_root, "a.zip"), ("a.txt", "a"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Extractor().Run(Single(zip, target, ArchiveType.Zip), cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Stale_staging_folders_are_removed()
    {
        string target = Dir("pkg");
        string stale = Path.Combine(target, ".ydl-extract-old");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk.txt"), "x");
        string zip = MakeZip(Path.Combine(_root, "a.zip"), ("a.txt", "a"));
        Extractor().Run(Single(zip, target, ArchiveType.Zip), CancellationToken.None);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void SevenZip_made_by_tar_is_extracted_with_tar()
    {
        Assert.True(File.Exists(Tar), "tar.exe fehlt");
        string src = Dir("src");
        File.WriteAllText(Path.Combine(src, "one.txt"), "eins");
        Directory.CreateDirectory(Path.Combine(src, "folder"));
        File.WriteAllText(Path.Combine(src, "folder", "two.txt"), "zwei");
        string archive = Path.Combine(_root, "single.7z");
        Assert.Equal(0, RunTar("-a", "-cf", archive, "-C", src, "one.txt", "folder"));

        string target = Dir("pkg");
        var r = Extractor(tar: Tar).Run(Single(archive, target, ArchiveType.SevenZip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("eins", File.ReadAllText(Path.Combine(target, "one.txt")));
        Assert.Equal("zwei", File.ReadAllText(Path.Combine(target, "folder", "two.txt")));
        Assert.Empty(Directory.GetDirectories(target, ".ydl-extract-*"));
    }

    [Fact]
    public void Tar_extraction_does_not_overwrite_either()
    {
        string src = Dir("src");
        File.WriteAllText(Path.Combine(src, "one.txt"), "neu");
        string archive = Path.Combine(_root, "single.7z");
        Assert.Equal(0, RunTar("-a", "-cf", archive, "-C", src, "one.txt"));
        string target = Dir("pkg");
        File.WriteAllText(Path.Combine(target, "one.txt"), "alt");
        var r = Extractor(tar: Tar).Run(Single(archive, target, ArchiveType.SevenZip), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("alt", File.ReadAllText(Path.Combine(target, "one.txt")));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(target, "one (1).txt")));
    }

    [Fact]
    public void Tar_with_no_space_is_refused()
    {
        string src = Dir("src");
        File.WriteAllText(Path.Combine(src, "one.txt"), new string('a', 5000));
        string archive = Path.Combine(_root, "single.7z");
        Assert.Equal(0, RunTar("-a", "-cf", archive, "-C", src, "one.txt"));
        string target = Dir("pkg");
        var r = Extractor(tar: Tar, space: _ => 10).Run(Single(archive, target, ArchiveType.SevenZip), CancellationToken.None);
        Assert.Equal(ExtractFailure.NoSpace, r.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Garbage_archive_with_tar_fails_without_throwing()
    {
        string archive = Path.Combine(_root, "garbage.7z");
        File.WriteAllBytes(archive, Enumerable.Range(0, 2000).Select(i => (byte)(i * 7)).ToArray());
        string target = Dir("pkg");
        var r = Extractor(tar: Tar).Run(Single(archive, target, ArchiveType.SevenZip), CancellationToken.None);
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.ToolFailed, r.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void ValidateStaging_rejects_junction()
    {
        string staging = Dir("staging");
        string outside = Dir("outside");
        File.WriteAllText(Path.Combine(outside, "x.txt"), "x");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(Path.Combine(staging, "link"));
        psi.ArgumentList.Add(outside);
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        Assert.Null(ArchiveExtractor.ValidateStaging(staging, CancellationToken.None));
        Directory.Delete(Path.Combine(staging, "link")); // nur die Verknüpfung, nicht das Ziel
        Assert.True(File.Exists(Path.Combine(outside, "x.txt")));
    }

    [Fact]
    public void ValidateStaging_lists_plain_files()
    {
        string staging = Dir("staging");
        Directory.CreateDirectory(Path.Combine(staging, "d"));
        File.WriteAllText(Path.Combine(staging, "a.txt"), "a");
        File.WriteAllText(Path.Combine(staging, "d", "b.txt"), "b");
        var files = ArchiveExtractor.ValidateStaging(staging, CancellationToken.None)!;
        Assert.Equal(new[] { "a.txt", Path.Combine("d", "b.txt") }, files.OrderBy(f => f.Length).ToArray());
    }

    [Fact]
    public void UniquePath_counts_up()
    {
        string a = Path.Combine(_root, "f.txt");
        Assert.Equal(a, ArchiveExtractor.UniquePath(a));
        File.WriteAllText(a, "1");
        Assert.Equal(Path.Combine(_root, "f (1).txt"), ArchiveExtractor.UniquePath(a));
        File.WriteAllText(Path.Combine(_root, "f (1).txt"), "2");
        Assert.Equal(Path.Combine(_root, "f (2).txt"), ArchiveExtractor.UniquePath(a));
    }

    [Fact]
    public void SevenZip_listing_is_parsed()
    {
        const string output = "Path = a.txt\r\nFolder = -\r\nSize = 12\r\nPacked Size = 10\r\nEncrypted = -\r\n\r\n" +
            "Path = sub\\b.txt\r\nFolder = -\r\nSize = 30\r\nEncrypted = +\r\n\r\n" +
            "Path = sub\r\nFolder = +\r\nSize = 0\r\n";
        var list = ArchiveExtractor.ParseSevenZipList(output);
        Assert.Equal(3, list.Count);
        Assert.Equal("a.txt", list[0].Path);
        Assert.Equal(12, list[0].Size);
        Assert.False(list[0].Encrypted);
        Assert.Equal("sub\\b.txt", list[1].Path);
        Assert.True(list[1].Encrypted);
        Assert.Equal(0, list[2].Size);
    }

    private static int RunTar(params string[] args)
    {
        var psi = new ProcessStartInfo(Tar) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
