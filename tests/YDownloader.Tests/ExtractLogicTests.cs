using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;
using YDownloader.Packages;

namespace YDownloader.Tests;

public class ExtractPlannerTests
{
    [Theory]
    // einfache ZIP: .NET
    [InlineData(ArchiveType.Zip, false, false, false, false, ExtractorKind.DotNet)]
    [InlineData(ArchiveType.Zip, false, false, true, true, ExtractorKind.DotNet)]
    // geschützte ZIP und Mehrteiliges: nur 7-Zip
    [InlineData(ArchiveType.Zip, false, true, true, true, ExtractorKind.SevenZip)]
    [InlineData(ArchiveType.Zip, true, false, true, false, ExtractorKind.SevenZip)]
    [InlineData(ArchiveType.Rar, true, false, true, true, ExtractorKind.SevenZip)]
    [InlineData(ArchiveType.SevenZip, true, false, true, false, ExtractorKind.SevenZip)]
    // einteilig RAR/7z: 7-Zip vor tar
    [InlineData(ArchiveType.Rar, false, false, true, true, ExtractorKind.SevenZip)]
    [InlineData(ArchiveType.Rar, false, false, false, true, ExtractorKind.Tar)]
    [InlineData(ArchiveType.SevenZip, false, false, false, true, ExtractorKind.Tar)]
    public void Choose_picks_by_capability(ArchiveType type, bool multi, bool encrypted, bool sevenZip, bool tar, ExtractorKind expected)
    {
        var c = ExtractPlanner.Choose(type, multi, encrypted, sevenZip, tar);
        Assert.Equal(expected, c.Extractor);
        Assert.Equal(ExtractState.None, c.Blocked);
    }

    [Theory]
    [InlineData(ArchiveType.Zip, false, false, true, true, true, "SevenZip,Tar")]    // fremdes Verfahren: .NET entfällt
    [InlineData(ArchiveType.Zip, false, false, true, false, true, "Tar")]
    [InlineData(ArchiveType.Zip, false, false, false, true, true, "DotNet,SevenZip,Tar")]
    [InlineData(ArchiveType.Zip, false, false, false, false, true, "DotNet,Tar")]
    [InlineData(ArchiveType.Zip, false, true, false, true, true, "SevenZip")]          // geschützt: kein tar
    [InlineData(ArchiveType.Rar, true, false, false, true, true, "SevenZip")]          // mehrteilig: kein tar
    [InlineData(ArchiveType.Zip, false, false, true, false, false, "")]
    public void Chain_lists_extractors_in_order(ArchiveType type, bool multi, bool encrypted, bool unsupported, bool sevenZip, bool tar, string expected)
    {
        var chain = ExtractPlanner.Chain(type, multi, encrypted, unsupported, sevenZip, tar);
        Assert.Equal(expected, string.Join(",", chain));
    }

    [Theory]
    [InlineData(ExtractState.Failed, ExtractFailure.Unreadable, true)]
    [InlineData(ExtractState.Failed, ExtractFailure.Corrupt, true)]
    [InlineData(ExtractState.Failed, ExtractFailure.UnsupportedMethod, true)]
    [InlineData(ExtractState.Failed, ExtractFailure.ToolFailed, true)]
    [InlineData(ExtractState.Failed, ExtractFailure.NoSpace, false)]
    [InlineData(ExtractState.Failed, ExtractFailure.UnsafePath, false)]
    [InlineData(ExtractState.Failed, ExtractFailure.Link, false)]
    [InlineData(ExtractState.Failed, ExtractFailure.MissingParts, false)]
    [InlineData(ExtractState.Failed, ExtractFailure.Timeout, false)]
    [InlineData(ExtractState.Failed, ExtractFailure.Io, false)]
    [InlineData(ExtractState.NeedPassword, ExtractFailure.None, false)]
    [InlineData(ExtractState.Done, ExtractFailure.None, false)]
    public void Next_extractor_is_tried_only_for_causes_another_one_may_fix(ExtractState state, ExtractFailure failure, bool expected) =>
        Assert.Equal(expected, ExtractPlanner.ShouldTryNext(new ExtractResult(state, failure)));

    [Fact]
    public void Final_failure_prefers_a_known_cause()
    {
        var tool = new ExtractResult(ExtractState.Failed, ExtractFailure.ToolFailed, "ERROR: x");
        var corrupt = new ExtractResult(ExtractState.Failed, ExtractFailure.Corrupt);
        var method = new ExtractResult(ExtractState.Failed, ExtractFailure.UnsupportedMethod);
        Assert.Equal(ExtractFailure.Unreadable, ExtractPlanner.FinalFailure(Array.Empty<ExtractResult>(), null).Failure);
        Assert.Equal(tool, ExtractPlanner.FinalFailure(new[] { tool }, null));
        Assert.Equal(corrupt, ExtractPlanner.FinalFailure(new[] { tool, corrupt, tool }, null));
        Assert.Equal(method, ExtractPlanner.FinalFailure(new[] { corrupt, method, tool }, null));
        var named = ExtractPlanner.FinalFailure(new[] { corrupt, tool }, "Zstandard");
        Assert.Equal(ExtractFailure.UnsupportedMethod, named.Failure);
        Assert.Equal("Zstandard", named.Detail);
    }

    [Theory]
    [InlineData("The archive entry was compressed using an unsupported compression method.", ExtractFailure.UnsupportedMethod)]
    [InlineData("tar.exe: Unsupported ZIP compression method (93: zstd)", ExtractFailure.UnsupportedMethod)]
    [InlineData("ERROR: Unsupported Method : a.txt", ExtractFailure.UnsupportedMethod)]
    [InlineData("ERROR: Data Error : a.txt", ExtractFailure.Corrupt)]
    [InlineData("ERROR: CRC Failed : a.txt", ExtractFailure.Corrupt)]
    [InlineData("WARNINGS: There are data after the end of archive\nERROR: Unexpected end of archive", ExtractFailure.Corrupt)]
    [InlineData("tar.exe: Error opening archive: Unrecognized archive format", ExtractFailure.Corrupt)]
    [InlineData("tar.exe: Truncated ZIP file data", ExtractFailure.Corrupt)]
    [InlineData("End of Central Directory record could not be found.", ExtractFailure.Corrupt)]
    [InlineData("ERROR: Cannot open the file as archive", ExtractFailure.Corrupt)]
    [InlineData("Access is denied", ExtractFailure.None)]
    [InlineData("", ExtractFailure.None)]
    public void Tool_messages_map_to_a_cause(string text, ExtractFailure expected) =>
        Assert.Equal(expected, ExtractErrors.Classify(text));

    [Theory]
    [InlineData(0, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(99, true)]
    [InlineData(93, false)]
    [InlineData(95, false)]
    [InlineData(14, false)]
    public void Dotnet_reads_only_stored_deflate_and_deflate64(int method, bool reads) =>
        Assert.Equal(reads, ZipMethods.DotNetReads(method));

    [Fact]
    public void Method_names_are_readable()
    {
        Assert.Equal("Zstandard", ZipMethods.Name(93));
        Assert.Equal("XZ", ZipMethods.Name(95));
        Assert.Equal("#777", ZipMethods.Name(777));
    }

    [Theory]
    [InlineData(ArchiveType.Rar, true, false, false, true)]      // mehrteilig, tar kann das nicht sicher
    [InlineData(ArchiveType.Zip, true, false, false, true)]
    [InlineData(ArchiveType.Zip, false, true, false, true)]      // geschützt
    [InlineData(ArchiveType.Rar, false, false, false, false)]    // gar nichts da
    public void Choose_needs_sevenzip_when_nothing_fits(ArchiveType type, bool multi, bool encrypted, bool sevenZip, bool tar)
    {
        var c = ExtractPlanner.Choose(type, multi, encrypted, sevenZip, tar);
        Assert.Null(c.Extractor);
        Assert.Equal(ExtractState.NeedSevenZip, c.Blocked);
    }

    [Theory]
    [InlineData(PartKind.RarPart, ArchiveType.Rar)]
    [InlineData(PartKind.RarOld, ArchiveType.Rar)]
    [InlineData(PartKind.SevenZip, ArchiveType.SevenZip)]
    [InlineData(PartKind.ZipSplit, ArchiveType.Zip)]
    [InlineData(PartKind.ZipOld, ArchiveType.Zip)]
    public void TypeOf_maps_kinds(PartKind kind, ArchiveType type) => Assert.Equal(type, ExtractPlanner.TypeOf(kind));

    private static PartFacts Done(string name, string? checksum = null, bool? ok = null) =>
        new(@"C:\dl\pkg\" + name, true, checksum, ok);

    [Fact]
    public void Evaluate_ready_when_all_parts_done_and_complete()
    {
        var parts = new[] { Done("a.part1.rar"), Done("a.part2.rar"), Done("a.part3.rar") };
        Assert.Equal(Readiness.Ready, ExtractPlanner.Evaluate(parts));
    }

    [Fact]
    public void Evaluate_waits_for_running_part()
    {
        var parts = new[] { Done("a.part1.rar"), new PartFacts(null, false, null, null), Done("a.part3.rar") };
        Assert.Equal(Readiness.NotReady, ExtractPlanner.Evaluate(parts));
    }

    [Fact]
    public void Evaluate_waits_when_completed_part_has_no_path()
    {
        var parts = new[] { Done("a.part1.rar"), new PartFacts(null, true, null, null) };
        Assert.Equal(Readiness.NotReady, ExtractPlanner.Evaluate(parts));
    }

    [Fact]
    public void Evaluate_reports_gap()
    {
        var parts = new[] { Done("a.part1.rar"), Done("a.part3.rar") };
        Assert.Equal(Readiness.Gap, ExtractPlanner.Evaluate(parts));
    }

    [Fact]
    public void Evaluate_reports_gap_when_first_part_missing()
    {
        var parts = new[] { Done("a.part2.rar"), Done("a.part3.rar") };
        Assert.Equal(Readiness.Gap, ExtractPlanner.Evaluate(parts));
    }

    [Fact]
    public void Evaluate_not_an_archive_is_gap()
    {
        var parts = new[] { Done("a.part1.rar"), Done("readme.txt") };
        Assert.Equal(Readiness.Gap, ExtractPlanner.Evaluate(parts));
    }

    [Fact]
    public void Evaluate_empty_is_not_ready() => Assert.Equal(Readiness.NotReady, ExtractPlanner.Evaluate(Array.Empty<PartFacts>()));

    [Fact]
    public void Evaluate_requires_confirmed_checksum_when_set()
    {
        Assert.Equal(Readiness.NotReady, ExtractPlanner.Evaluate(new[]
        {
            Done("a.7z.001", "sha256:ab", null), Done("a.7z.002"),
        }));
        Assert.Equal(Readiness.NotReady, ExtractPlanner.Evaluate(new[]
        {
            Done("a.7z.001", "sha256:ab", false), Done("a.7z.002"),
        }));
        Assert.Equal(Readiness.Ready, ExtractPlanner.Evaluate(new[]
        {
            Done("a.7z.001", "sha256:ab", true), Done("a.7z.002"),
        }));
    }

    [Fact]
    public void Evaluate_old_rar_needs_the_rar()
    {
        Assert.Equal(Readiness.Gap, ExtractPlanner.Evaluate(new[] { Done("a.r00"), Done("a.r01") }));
        Assert.Equal(Readiness.Ready, ExtractPlanner.Evaluate(new[] { Done("a.rar"), Done("a.r00"), Done("a.r01") }));
    }

    [Theory]
    [InlineData(new[] { "a.part2.rar", "a.part1.rar", "a.part3.rar" }, 1)]
    [InlineData(new[] { "a.r00", "a.rar", "a.r01" }, 1)]
    [InlineData(new[] { "a.7z.002", "a.7z.001" }, 1)]
    [InlineData(new[] { "a.zip.002", "a.zip.001" }, 1)]
    [InlineData(new[] { "a.z01", "a.zip", "a.z02" }, 1)]
    [InlineData(new[] { "a.z01", "a.z02", "a.zip" }, 2)]
    public void EntryIndex_finds_first_file(string[] names, int expected) =>
        Assert.Equal(expected, ExtractPlanner.EntryIndex(names.Select(n => @"C:\x\" + n).ToList()));

    [Theory]
    [InlineData("a.zip", ArchiveType.Zip)]
    [InlineData("A.ZIP", ArchiveType.Zip)]
    [InlineData("a.rar", ArchiveType.Rar)]
    [InlineData("a.7z", ArchiveType.SevenZip)]
    [InlineData(@"C:\x\Film.2024.7z", ArchiveType.SevenZip)]
    public void SingleType_reads_extension(string name, ArchiveType type) => Assert.Equal(type, ExtractPlanner.SingleType(name));

    [Theory]
    [InlineData("a.part1.rar")]
    [InlineData("a.7z.001")]
    [InlineData("a.zip.001")]
    [InlineData("a.r00")]
    [InlineData("a.z01")]
    [InlineData("a.tar")]
    [InlineData("a.gz")]
    [InlineData("a.exe")]
    [InlineData("a")]
    [InlineData("")]
    public void SingleType_ignores_parts_and_other_files(string name)
    {
        Assert.Null(ExtractPlanner.SingleType(name));
        Assert.False(ExtractPlanner.IsSingleArchive(name));
    }

    [Fact]
    public void SingleTargetDir_is_subfolder_named_after_archive()
    {
        Assert.Equal(@"C:\dl\Film", ExtractPlanner.SingleTargetDir(@"C:\dl\Film.zip"));
        Assert.Equal(@"C:\dl\My.Show.S01", ExtractPlanner.SingleTargetDir(@"C:\dl\My.Show.S01.7z"));
        Assert.Equal(@"C:\dl\a b", ExtractPlanner.SingleTargetDir(@"C:\dl\a b.rar"));
    }

    [Theory]
    [InlineData("a.rar", "a.r00", true)]
    [InlineData("a.rar", "A.R01", true)]
    [InlineData("a.zip", "a.z01", true)]
    [InlineData("a.rar", "b.r00", false)]
    [InlineData("a.rar", "a.zip", false)]
    [InlineData("a.zip", "a.txt", false)]
    [InlineData("a.7z", "a.7z.001", false)]
    [InlineData("a.rar", "a.rar", true)]
    public void IsSiblingPart_finds_parts_of_same_archive(string archive, string other, bool expected) =>
        Assert.Equal(expected, ExtractPlanner.IsSiblingPart(archive, other));

    [Fact]
    public void EvaluateSingle_needs_done_and_confirmed_checksum()
    {
        Assert.Equal(Readiness.Ready, ExtractPlanner.EvaluateSingle(new(@"C:\a.zip", true, null, null)));
        Assert.Equal(Readiness.NotReady, ExtractPlanner.EvaluateSingle(new(@"C:\a.zip", false, null, null)));
        Assert.Equal(Readiness.NotReady, ExtractPlanner.EvaluateSingle(new(null, true, null, null)));
        Assert.Equal(Readiness.NotReady, ExtractPlanner.EvaluateSingle(new(@"C:\a.zip", true, "sha256:ab", null)));
        Assert.Equal(Readiness.NotReady, ExtractPlanner.EvaluateSingle(new(@"C:\a.zip", true, "sha256:ab", false)));
        Assert.Equal(Readiness.Ready, ExtractPlanner.EvaluateSingle(new(@"C:\a.zip", true, "sha256:ab", true)));
    }

    [Fact]
    public void EntryIndex_rejects_mixed_or_unknown()
    {
        Assert.Equal(-1, ExtractPlanner.EntryIndex(new[] { "a.part1.rar", "b.part2.rar" }));
        Assert.Equal(-1, ExtractPlanner.EntryIndex(new[] { "a.part1.rar", "a.txt" }));
        Assert.Equal(-1, ExtractPlanner.EntryIndex(Array.Empty<string>()));
    }
}

public class PathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ydl-guard-root");

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/a.txt")]
    [InlineData("dir\\sub\\a.txt")]
    [InlineData("./a.txt")]
    [InlineData("dir//a.txt")]
    [InlineData("dir/")]
    [InlineData("Ünï cöde/日本.txt")]
    public void Resolve_accepts_normal_names(string name)
    {
        string? full = PathGuard.Resolve(Root, name);
        Assert.NotNull(full);
        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("dir/../../evil.txt")]
    [InlineData("dir/../ok.txt")]          // auch wenn es im Ordner bliebe
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\x.dll")]
    [InlineData("C:\\Windows\\x.dll")]
    [InlineData("C:x.dll")]
    [InlineData("a.txt:stream")]
    [InlineData(".. /x.txt")]
    [InlineData("...\\x.txt")]
    [InlineData("dir/con")]
    [InlineData("NUL.txt")]
    [InlineData("aux")]
    [InlineData("a\0b.txt")]
    [InlineData("a<b.txt")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("\\\\server\\share\\x")]
    public void Resolve_rejects_unsafe_names(string name) => Assert.Null(PathGuard.Resolve(Root, name));

    [Fact]
    public void Resolve_does_not_confuse_sibling_prefix()
    {
        // "ydl-guard-root-evil" beginnt wie der Ordner, liegt aber daneben
        Assert.Null(PathGuard.Resolve(Root, "../ydl-guard-root-evil/x.txt"));
    }

    [Fact]
    public void Resolve_handles_root_with_trailing_separator()
    {
        Assert.NotNull(PathGuard.Resolve(Root + Path.DirectorySeparatorChar, "a.txt"));
    }
}

public class SpaceCheckTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void Required_adds_tenth_or_minimum_reserve()
    {
        Assert.Equal(100 * MB + 256 * MB, SpaceCheck.Required(100 * MB));
        Assert.Equal(10_000 * MB + 1_000 * MB, SpaceCheck.Required(10_000 * MB));
        Assert.Equal(256 * MB, SpaceCheck.Required(0));
        Assert.Equal(256 * MB, SpaceCheck.Required(-5));
    }

    [Fact]
    public void Enough_compares_with_reserve()
    {
        Assert.True(SpaceCheck.Enough(100 * MB, 356 * MB));
        Assert.False(SpaceCheck.Enough(100 * MB, 356 * MB - 1));
    }

    [Fact]
    public void Bomb_does_not_fit()
    {
        Assert.False(SpaceCheck.Enough(5L * 1024 * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Huge_values_do_not_overflow()
    {
        Assert.Equal(long.MaxValue, SpaceCheck.Required(long.MaxValue));
        Assert.Equal(long.MaxValue, SpaceCheck.Estimate(long.MaxValue));
        Assert.Equal(50, SpaceCheck.Estimate(5));
    }
}

public class ToolLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ydl-tools-" + Guid.NewGuid().ToString("N"));

    public ToolLocatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Make(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private ToolSearchEnv Env(string? pf = null, string? pfx86 = null, string? path = null, params string[] forbidden) =>
        new(pf, pfx86, path, forbidden, File.Exists);

    [Fact]
    public void Configured_file_wins()
    {
        string mine = Make("mine", "7z.exe");
        string pf = Make("pf", "7-Zip", "7z.exe");
        Assert.Equal(mine, ToolLocator.FindSevenZip(mine, Env(Path.Combine(_root, "pf"))));
        Assert.NotEqual(pf, mine);
    }

    [Fact]
    public void Configured_folder_is_accepted()
    {
        string mine = Make("mine", "7z.exe");
        Assert.Equal(mine, ToolLocator.FindSevenZip(Path.GetDirectoryName(mine), Env()));
    }

    [Fact]
    public void Missing_configured_falls_through_to_program_files()
    {
        string pf = Make("pf", "7-Zip", "7z.exe");
        Assert.Equal(pf, ToolLocator.FindSevenZip(Path.Combine(_root, "nope", "7z.exe"), Env(Path.Combine(_root, "pf"))));
    }

    [Fact]
    public void Order_is_programfiles_then_x86_then_path()
    {
        string x86 = Make("x86", "7-Zip", "7z.exe");
        string shim = Make("scoop", "shims", "7z.exe");
        string path = string.Join(Path.PathSeparator, Path.Combine(_root, "scoop", "shims"));
        Assert.Equal(x86, ToolLocator.FindSevenZip(null, Env(Path.Combine(_root, "pf"), Path.Combine(_root, "x86"), path)));
        Assert.Equal(shim, ToolLocator.FindSevenZip(null, Env(Path.Combine(_root, "pf"), Path.Combine(_root, "nope"), path)));
    }

    [Fact]
    public void Path_entry_inside_forbidden_folder_is_skipped()
    {
        Make("downloads", "evil", "7z.exe");
        string good = Make("tools", "7z.exe");
        string path = string.Join(Path.PathSeparator, Path.Combine(_root, "downloads", "evil"), Path.Combine(_root, "tools"));
        Assert.Equal(good, ToolLocator.FindSevenZip(null, Env(null, null, path, Path.Combine(_root, "downloads"))));
    }

    [Fact]
    public void Configured_path_inside_forbidden_folder_is_refused()
    {
        string evil = Make("app", "7z.exe");
        Assert.Null(ToolLocator.FindSevenZip(evil, Env(null, null, null, Path.Combine(_root, "app"))));
    }

    [Fact]
    public void Relative_path_entries_are_ignored()
    {
        Assert.Null(ToolLocator.FindSevenZip(null, Env(null, null, "." + Path.PathSeparator + "tools" + Path.PathSeparator + "")));
        Assert.Null(ToolLocator.FindSevenZip("7z.exe", Env()));
    }

    [Fact]
    public void Not_found_is_null() => Assert.Null(ToolLocator.FindSevenZip(null, Env()));

    [Fact]
    public void FindTar_checks_system_folder_only()
    {
        string tar = Make("sys", "tar.exe");
        Assert.Equal(tar, ToolLocator.FindTar(Path.Combine(_root, "sys"), File.Exists));
        Assert.Null(ToolLocator.FindTar(Path.Combine(_root, "empty"), File.Exists));
    }
}

public class ZipProbeTests
{
    private static byte[] MakeZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("a.txt").Open())) w.Write("hello");
            using (var w2 = new StreamWriter(zip.CreateEntry("dir/b.txt").Open())) w2.Write("world");
        }
        return ms.ToArray();
    }

    [Fact]
    public void Plain_zip_is_not_encrypted() => Assert.False(ZipProbe.IsEncrypted(new MemoryStream(MakeZip())));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Encrypted_flag_in_central_directory_is_found(int entry)
    {
        byte[] zip = MakeZip();
        // n-ten Eintrag des zentralen Verzeichnisses suchen und Bit 0 der Flags setzen
        int pos = 0, seen = 0;
        for (int i = 0; i < zip.Length - 4; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 1 && zip[i + 3] == 2)
            {
                if (seen++ == entry) { zip[i + 8] |= 1; pos = i; break; }
            }
        }
        Assert.True(pos > 0);
        Assert.True(ZipProbe.IsEncrypted(new MemoryStream(zip)));
    }

    [Fact]
    public void Garbage_and_tiny_files_are_not_reported_encrypted()
    {
        Assert.False(ZipProbe.IsEncrypted(new MemoryStream(new byte[] { 1, 2, 3 })));
        Assert.False(ZipProbe.IsEncrypted(new MemoryStream(new byte[0])));
        Assert.False(ZipProbe.IsEncrypted(new MemoryStream(Enumerable.Repeat((byte)7, 5000).ToArray())));
    }
}
