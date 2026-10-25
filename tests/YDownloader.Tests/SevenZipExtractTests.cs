using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using YDownloader.Packages;

namespace YDownloader.Tests;

/// <summary>
/// Läuft gegen ein echtes 7-Zip, wenn eines gefunden wird (Programme, PATH). Ohne 7-Zip tun die Tests nichts: Dann bleibt der
/// 7-Zip-Weg ungeprüft. Alle Archive werden hier selbst erzeugt. RAR kann 7-Zip nicht schreiben und bleibt ungeprüft.
/// </summary>
public class SevenZipExtractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ydl-7z-" + Guid.NewGuid().ToString("N"));
    private static readonly string? SevenZip = ToolLocator.FindSevenZip(null, ToolSearchEnv.Current(Array.Empty<string>()));

    public SevenZipExtractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Sub(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static int Seven(params string[] args)
    {
        var psi = new ProcessStartInfo(SevenZip!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private string MakeSource()
    {
        string src = Sub("src");
        var rnd = new Random(42);
        var data = new byte[20_000];
        rnd.NextBytes(data); // nicht komprimierbar: der Archivteil wird groß genug für mehrere Teile
        File.WriteAllBytes(Path.Combine(src, "data.bin"), data);
        Directory.CreateDirectory(Path.Combine(src, "ordner"));
        File.WriteAllText(Path.Combine(src, "ordner", "text.txt"), "Grüße");
        return src;
    }

    private static ArchiveExtractor Extractor(Func<string, long?>? space = null) =>
        new(() => SevenZip, () => null, space ?? (_ => long.MaxValue));

    [Fact]
    public void Multipart_7z_is_extracted()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string pkg = Sub("pkg");
        Assert.Equal(0, Seven("a", "-v6k", Path.Combine(pkg, "pack.7z"), Path.Combine(src, "*")));
        string[] parts = Directory.GetFiles(pkg, "pack.7z.*").OrderBy(f => f).ToArray();
        Assert.True(parts.Length >= 3, "erwartet mehrere Teile");

        string target = Sub("out");
        var job = new ExtractJob(parts[0], target, ArchiveType.SevenZip, true, parts);
        var r = Extractor().Run(job, CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(2, r.Files);
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "data.bin")), File.ReadAllBytes(Path.Combine(target, "data.bin")));
        Assert.Equal("Grüße", File.ReadAllText(Path.Combine(target, "ordner", "text.txt")));
        Assert.All(parts, p => Assert.True(File.Exists(p))); // Archive bleiben
        Assert.Empty(Directory.GetDirectories(target, ".ydl-extract-*"));
    }

    [Fact]
    public void Existing_files_are_kept_with_7z()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "a.7z");
        Assert.Equal(0, Seven("a", archive, Path.Combine(src, "ordner")));
        string target = Sub("out");
        Directory.CreateDirectory(Path.Combine(target, "ordner"));
        File.WriteAllText(Path.Combine(target, "ordner", "text.txt"), "alt");
        var r = Extractor().Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("alt", File.ReadAllText(Path.Combine(target, "ordner", "text.txt")));
        Assert.Equal("Grüße", File.ReadAllText(Path.Combine(target, "ordner", "text (1).txt")));
    }

    [Fact]
    public void Password_protected_needs_password()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "secret.7z");
        Assert.Equal(0, Seven("a", "-pSecret", archive, Path.Combine(src, "ordner")));
        string target = Sub("out");
        var r = Extractor().Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractState.NeedPassword, r.State);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Single_7z_with_password_makes_no_folder()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "Einzeln.7z");
        Assert.Equal(0, Seven("a", "-pSecret", archive, Path.Combine(src, "ordner")));
        string target = ExtractPlanner.SingleTargetDir(archive);
        var r = Extractor().Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractState.NeedPassword, r.State);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Single_7z_goes_into_subfolder()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "Einzeln.7z");
        Assert.Equal(0, Seven("a", archive, Path.Combine(src, "ordner")));
        string target = ExtractPlanner.SingleTargetDir(archive);
        var r = Extractor().Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("Grüße", File.ReadAllText(Path.Combine(target, "ordner", "text.txt")));
    }

    [Fact]
    public void Header_encrypted_needs_password()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "hidden.7z");
        Assert.Equal(0, Seven("a", "-pSecret", "-mhe=on", archive, Path.Combine(src, "ordner")));
        string target = Sub("out");
        var r = Extractor().Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractState.NeedPassword, r.State);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Encrypted_zip_needs_password_with_sevenzip()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "secret.zip");
        Assert.Equal(0, Seven("a", "-pSecret", "-tzip", archive, Path.Combine(src, "ordner")));
        string target = Sub("out");
        var r = Extractor().Run(new ExtractJob(archive, target, ArchiveType.Zip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractState.NeedPassword, r.State);
    }

    // ---------------------------------------------------------------- Passwörter (ausgedachte, nur für selbst erzeugte Archive)

    private ExtractResult RunWith(string archive, ArchiveType type, bool multi, string[] parts, string[]? passwords, string? target = null,
        TimeSpan? timeout = null)
    {
        target ??= Sub("out");
        var extractor = new ArchiveExtractor(() => SevenZip, () => null, _ => long.MaxValue, timeout);
        return extractor.Run(new ExtractJob(archive, target, type, multi, parts, passwords), CancellationToken.None);
    }

    [Theory]
    [InlineData("7z", false)]
    [InlineData("7z", true)] // verschlüsselte Dateinamen
    [InlineData("zip", false)]
    public void Right_password_from_the_list_extracts(string kind, bool encryptNames)
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "geschuetzt." + kind);
        var args = new List<string> { "a", "-pGeheim 123", "-t" + (kind == "zip" ? "zip" : "7z") };
        if (encryptNames) args.Add("-mhe=on");
        args.Add(archive);
        args.Add(Path.Combine(src, "ordner"));
        Assert.Equal(0, Seven(args.ToArray()));
        string target = Sub("out");

        var r = RunWith(archive, kind == "zip" ? ArchiveType.Zip : ArchiveType.SevenZip, false, new[] { archive },
            new[] { "falsch1", "falsch2", "Geheim 123", "nie probiert" }, target);

        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("Geheim 123", r.UsedPassword);
        Assert.Equal("Grüße", File.ReadAllText(Path.Combine(target, "ordner", "text.txt")));
        Assert.Empty(Directory.GetDirectories(target, ".ydl-extract-*"));
    }

    [Fact]
    public void Password_with_umlauts_and_symbols_reaches_7zip_intact()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "umlaut.7z");
        Assert.Equal(0, Seven("a", "-pGrüße ß € ä", archive, Path.Combine(src, "ordner")));
        var r = RunWith(archive, ArchiveType.SevenZip, false, new[] { archive }, new[] { "Grüße ß € ä" });
        Assert.Equal(ExtractState.Done, r.State);
    }

    [Theory]
    [InlineData("x")]       // steckt im Dateinamen des Archivinhalts: darf die Ausgabe nicht verfälschen
    [InlineData("t")]
    [InlineData("a b")]
    public void Short_passwords_that_occur_in_file_names_still_work(string password)
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "kurz.7z");
        Assert.Equal(0, Seven("a", "-p" + password, archive, Path.Combine(src, "ordner")));
        var r = RunWith(archive, ArchiveType.SevenZip, false, new[] { archive }, new[] { "falsch", password });
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(password, r.UsedPassword);
    }

    [Fact]
    public void Multipart_with_password_extracts()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string pkg = Sub("pkg");
        Assert.Equal(0, Seven("a", "-v6k", "-pgeheim123", Path.Combine(pkg, "pack.7z"), Path.Combine(src, "*")));
        string[] parts = Directory.GetFiles(pkg, "pack.7z.*").OrderBy(f => f).ToArray();
        Assert.True(parts.Length >= 3);
        string target = Sub("out");
        var r = RunWith(parts[0], ArchiveType.SevenZip, true, parts, new[] { "nein", "geheim123" }, target);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal("geheim123", r.UsedPassword);
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "data.bin")), File.ReadAllBytes(Path.Combine(target, "data.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_password_fits_reports_the_count_and_leaves_nothing(bool encryptNames)
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "zu.7z");
        var args = new List<string> { "a", "-pgeheim123" };
        if (encryptNames) args.Add("-mhe=on");
        args.AddRange(new[] { archive, Path.Combine(src, "ordner") });
        Assert.Equal(0, Seven(args.ToArray()));
        string target = ExtractPlanner.SingleTargetDir(archive);

        var r = RunWith(archive, ArchiveType.SevenZip, false, new[] { archive }, new[] { "a", "b", "c" }, target);

        Assert.Equal(ExtractState.NeedPassword, r.State);
        Assert.Equal("3", r.Detail);
        Assert.Null(r.UsedPassword);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Unprotected_archive_ignores_the_password_list()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "offen.7z");
        Assert.Equal(0, Seven("a", archive, Path.Combine(src, "ordner")));
        var r = RunWith(archive, ArchiveType.SevenZip, false, new[] { archive }, new[] { "irgendwas" });
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Null(r.UsedPassword);
    }

    [Fact]
    public void More_than_fifty_passwords_are_not_all_tried()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "viele.7z");
        Assert.Equal(0, Seven("a", "-pgeheim123", archive, Path.Combine(src, "ordner")));
        var list = Enumerable.Range(0, 60).Select(i => "falsch" + i).Append("geheim123").ToArray();
        var r = RunWith(archive, ArchiveType.SevenZip, false, new[] { archive }, list);
        Assert.Equal(ExtractState.NeedPassword, r.State); // das richtige steht hinter dem Limit
        Assert.Equal(PasswordRules.MaxCandidates.ToString(), r.Detail);
    }

    [Fact]
    public void A_hanging_attempt_ends_with_timeout_and_not_with_need_password()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "t.7z");
        Assert.Equal(0, Seven("a", archive, Path.Combine(src, "ordner")));
        var r = RunWith(archive, ArchiveType.SevenZip, false, new[] { archive }, null, timeout: TimeSpan.FromMilliseconds(1));
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Equal(ExtractFailure.Timeout, r.Failure);
    }

    [Fact]
    public void Password_never_appears_on_the_command_line_or_in_results()
    {
        // Ein Programm, das nur seine Argumente und seine Standardeingabe aufzeichnet und immer „Wrong password“ meldet.
        string fake = Path.Combine(_root, "fake7z.cmd");
        string log = Path.Combine(_root, "args.log");
        string stdin = Path.Combine(_root, "stdin.log");
        File.WriteAllText(fake,
            "@echo off\r\n" +
            $"echo %* >> \"{log}\"\r\n" +
            $"more >> \"{stdin}\"\r\n" +
            "echo ERROR: Wrong password : Streng geheim\r\n" +
            "exit /b 2\r\n");
        string archive = Path.Combine(_root, "x.7z");
        File.WriteAllBytes(archive, new byte[] { 1, 2, 3 });
        string target = Sub("out");

        var extractor = new ArchiveExtractor(() => fake, () => null, _ => long.MaxValue);
        var r = extractor.Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }, new[] { "Streng geheim" }),
            CancellationToken.None);

        Assert.Equal(ExtractState.NeedPassword, r.State);
        string args = File.ReadAllText(log);
        Assert.DoesNotContain("geheim", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-p", args);
        Assert.Contains("Streng geheim", File.ReadAllText(stdin)); // kam über die Standardeingabe an
        Assert.DoesNotContain("Streng geheim", r.Detail ?? "");
    }

    [Fact]
    public void Missing_volume_fails_and_cleans_up()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string pkg = Sub("pkg");
        Assert.Equal(0, Seven("a", "-v6k", Path.Combine(pkg, "pack.7z"), Path.Combine(src, "*")));
        string[] parts = Directory.GetFiles(pkg, "pack.7z.*").OrderBy(f => f).ToArray();
        File.Delete(parts[1]);
        string target = Sub("out");
        var r = Extractor().Run(new ExtractJob(parts[0], target, ArchiveType.SevenZip, true, new[] { parts[0], parts[2] }), CancellationToken.None);
        Assert.Equal(ExtractState.Failed, r.State);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Not_enough_space_stops_before_extracting_with_7z()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "a.7z");
        Assert.Equal(0, Seven("a", archive, Path.Combine(src, "data.bin")));
        string target = Sub("out");
        var r = Extractor(_ => 1000).Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), CancellationToken.None);
        Assert.Equal(ExtractFailure.NoSpace, r.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void Cancel_kills_process_and_cleans_up()
    {
        if (SevenZip is null) return;
        string src = MakeSource();
        string archive = Path.Combine(_root, "a.7z");
        Assert.Equal(0, Seven("a", archive, Path.Combine(src, "data.bin")));
        string target = Sub("out");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Extractor().Run(new ExtractJob(archive, target, ArchiveType.SevenZip, false, new[] { archive }), cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }
}
