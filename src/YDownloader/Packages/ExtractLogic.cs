using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace YDownloader.Packages;

/// <summary>Zustand des Entpackens eines Pakets. Wird mit gespeichert (außer <see cref="Extracting"/>, das nach einem Neustart nichts mehr aussagt).</summary>
public enum ExtractState
{
    None,
    Extracting,
    Done,
    /// <summary>Das Archiv braucht 7-Zip (mehrteilig oder geschützt), und es wurde keins gefunden.</summary>
    NeedSevenZip,
    /// <summary>Das Archiv ist passwortgeschützt und keins der bekannten Passwörter passt. Das Menü „Passwort eingeben …“ gibt ein neues.</summary>
    NeedPassword,
    Failed,
}

/// <summary>Kurzer Grund bei <see cref="ExtractState.Failed"/>. Der Text dazu entsteht erst bei der Anzeige.</summary>
public enum ExtractFailure
{
    None,
    NoSpace,
    UnsafePath,
    Link,
    MissingParts,
    Unreadable,
    ToolFailed,
    Io,
    /// <summary>Ein Aufruf von 7-Zip blieb über das Zeitlimit hinaus hängen.</summary>
    Timeout,
}

public enum ExtractorKind
{
    /// <summary>Eingebautes <c>System.IO.Compression</c>, nur einteilige, ungeschützte ZIP.</summary>
    DotNet,
    SevenZip,
    /// <summary><c>tar.exe</c> aus Windows (libarchive): einteilige RAR und 7z.</summary>
    Tar,
}

public enum ArchiveType
{
    Zip,
    Rar,
    SevenZip,
}

/// <summary>Entweder ein Entpacker oder der Zustand, in dem das Paket stehen bleibt.</summary>
public sealed record ExtractChoice(ExtractorKind? Extractor, ExtractState Blocked);

public enum Readiness
{
    /// <summary>Noch nicht alle Teile fertig (oder eine gesetzte Prüfsumme ist nicht bestätigt).</summary>
    NotReady,
    /// <summary>Alle Teile fertig, vollständig.</summary>
    Ready,
    /// <summary>Alle Teile fertig, aber die Namen zeigen eine Lücke oder gehören nicht zu einem Archiv.</summary>
    Gap,
}

/// <param name="Path">Tatsächlicher Pfad der fertigen Datei, sonst null.</param>
/// <param name="ChecksumOk">Ergebnis der Prüfsummen-Prüfung, null = nicht geprüft.</param>
public sealed record PartFacts(string? Path, bool Completed, string? Checksum, bool? ChecksumOk);

/// <summary>
/// Reine Entscheidungen für das Auto-Entpacken: welcher Entpacker, wann ein Paket bereit ist, welche Datei den Anfang
/// bildet. Ohne Oberfläche, Dateien und Prozesse, damit sich alles testen lässt.
/// </summary>
public static class ExtractPlanner
{
    public static ArchiveType TypeOf(PartKind kind) => kind switch
    {
        PartKind.RarPart or PartKind.RarOld => ArchiveType.Rar,
        PartKind.SevenZip => ArchiveType.SevenZip,
        _ => ArchiveType.Zip,
    };

    /// <summary>
    /// Wahl nach Fähigkeit: einfache, ungeschützte ZIP mit .NET. Mehrteiliges und Geschütztes nur mit 7-Zip. Sonst einteilige
    /// RAR und 7z mit 7-Zip, ersatzweise <c>tar.exe</c>. Fehlt, was nötig ist, bleibt das Paket bei „7-Zip nötig“.
    /// </summary>
    public static ExtractChoice Choose(ArchiveType type, bool multiPart, bool encrypted, bool sevenZipAvailable, bool tarAvailable)
    {
        if (type == ArchiveType.Zip && !multiPart && !encrypted) return new(ExtractorKind.DotNet, ExtractState.None);
        if (sevenZipAvailable) return new(ExtractorKind.SevenZip, ExtractState.None);
        if (!multiPart && !encrypted && tarAvailable) return new(ExtractorKind.Tar, ExtractState.None);
        return new(null, ExtractState.NeedSevenZip);
    }

    /// <summary>
    /// Ist das Paket bereit? Alle Teile fertig, eine gesetzte Prüfsumme bestätigt und keine Lücke in den Namen
    /// (<see cref="PackageDetector.Group{T}"/> meldet fehlende Teile).
    /// </summary>
    public static Readiness Evaluate(IReadOnlyList<PartFacts> parts)
    {
        if (parts.Count == 0) return Readiness.NotReady;
        foreach (var p in parts)
        {
            if (!p.Completed || string.IsNullOrEmpty(p.Path)) return Readiness.NotReady;
            if (!string.IsNullOrWhiteSpace(p.Checksum) && p.ChecksumOk != true) return Readiness.NotReady;
        }
        var groups = PackageDetector.Group(parts, p => System.IO.Path.GetFileName(p.Path!));
        if (groups.Count != 1 || !groups[0].IsPackage || groups[0].Missing.Count > 0) return Readiness.Gap;
        return Readiness.Ready;
    }

    /// <summary>
    /// Art eines einzelnen Archivs nach der Endung (<c>.zip</c>, <c>.rar</c>, <c>.7z</c>), sonst null. Teile eines mehrteiligen
    /// Archivs (<c>.part1.rar</c>) sind keine einzelnen Archive.
    /// </summary>
    public static ArchiveType? SingleType(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (PackageDetector.Parse(name) is { Kind: PartKind.RarPart }) return null;
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".zip" => ArchiveType.Zip,
            ".rar" => ArchiveType.Rar,
            ".7z" => ArchiveType.SevenZip,
            _ => null,
        };
    }

    public static bool IsSingleArchive(string fileName) => SingleType(fileName) is not null;

    /// <summary>Zielordner für ein einzelnes Archiv: ein Unterordner mit dem Archivnamen (ohne Endung) neben dem Archiv.</summary>
    public static string SingleTargetDir(string archivePath) =>
        Path.Combine(Path.GetDirectoryName(archivePath) ?? "", PackageDetector.FolderName(Path.GetFileNameWithoutExtension(archivePath)));

    /// <summary>
    /// Gehört <paramref name="other"/> zum selben mehrteiligen Archiv wie <paramref name="archive"/> (<c>a.rar</c> und <c>a.r00</c>,
    /// <c>a.zip</c> und <c>a.z01</c>)? Dann ist das Archiv nicht allein und wird nicht einzeln entpackt.
    /// </summary>
    public static bool IsSiblingPart(string archive, string other)
    {
        var a = PackageDetector.Parse(Path.GetFileName(archive));
        var b = PackageDetector.Parse(Path.GetFileName(other));
        return a is not null && b is not null && a.Key == b.Key;
    }

    /// <summary>Bereitschaft eines einzelnen Archivs: fertig, und eine gesetzte Prüfsumme ist bestätigt.</summary>
    public static Readiness EvaluateSingle(PartFacts part)
    {
        if (!part.Completed || string.IsNullOrEmpty(part.Path)) return Readiness.NotReady;
        if (!string.IsNullOrWhiteSpace(part.Checksum) && part.ChecksumOk != true) return Readiness.NotReady;
        return Readiness.Ready;
    }

    /// <summary>
    /// Index der Datei, mit der der Entpacker beginnt: bei <c>.partN.rar</c>, <c>.7z.001</c> und <c>.zip.001</c> der erste Teil,
    /// bei <c>.rar</c> + <c>.r00</c> die <c>.rar</c>, bei <c>.z01</c> + <c>.zip</c> die <c>.zip</c> (sie kommt zuletzt, enthält aber
    /// das Verzeichnis). -1, wenn die Namen nicht zu genau einem Archiv gehören.
    /// </summary>
    public static int EntryIndex(IReadOnlyList<string> fileNames)
    {
        PartName? first = null;
        int best = -1;
        for (int i = 0; i < fileNames.Count; i++)
        {
            var p = PackageDetector.Parse(Path.GetFileName(fileNames[i]));
            if (p is null) return -1;
            if (first is null) { first = p; best = i; continue; }
            if (p.Key != first.Key) return -1;
            var current = PackageDetector.Parse(Path.GetFileName(fileNames[best]))!;
            bool better = p.Kind == PartKind.ZipOld ? p.Order > current.Order : p.Order < current.Order;
            if (better) best = i;
        }
        return best;
    }
}

/// <summary>Schutz vor Pfaden, die aus dem Zielordner herausführen (Zip-Slip) und vor Namen, die Windows anders liest.</summary>
public static class PathGuard
{
    private static readonly Regex Reserved = new(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Der volle Pfad eines Archiveintrags unter <paramref name="root"/>, sonst null. Abgelehnt werden absolute Pfade,
    /// Laufwerke, Datenströme (<c>:</c>), <c>..</c> (auch wenn es im Ordner bliebe), Segmente aus nur Punkten und Leerzeichen
    /// (Windows liest <c>.. </c> als <c>..</c>) und Gerätenamen wie <c>CON</c>. Ein Eintrag, der nur den Ordner selbst meint, gilt als null.
    /// </summary>
    public static string? Resolve(string root, string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\0')) return null;
        string name = entryName.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':')) return null;

        var parts = new List<string>();
        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment.Trim(' ', '.').Length == 0) return null;          // "..", ".. ", "..."
            if (Reserved.IsMatch(segment.TrimEnd(' ', '.'))) return null;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            parts.Add(segment);
        }
        if (parts.Count == 0) return null;

        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar, parts)));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>Platzprüfung vor dem Entpacken. Fängt Archivbomben ab: Die entpackte Größe steht im Verzeichnis des Archivs.</summary>
public static class SpaceCheck
{
    public const long MinReserve = 256L * 1024 * 1024;

    /// <summary>Faktor, wenn die entpackte Größe unbekannt ist (etwa bei <c>tar.exe</c>): das Zehnfache des Archivs.</summary>
    public const int UnknownFactor = 10;

    /// <summary>Platz, der frei sein muss: die entpackte Größe plus Reserve (ein Zehntel, mindestens 256 MB).</summary>
    public static long Required(long uncompressed)
    {
        if (uncompressed < 0) uncompressed = 0;
        long reserve = Math.Max(uncompressed / 10, MinReserve);
        return uncompressed > long.MaxValue - reserve ? long.MaxValue : uncompressed + reserve;
    }

    /// <summary>Schätzung, wenn die entpackte Größe nicht zu lesen ist.</summary>
    public static long Estimate(long archiveBytes) =>
        archiveBytes > long.MaxValue / UnknownFactor ? long.MaxValue : Math.Max(0, archiveBytes) * UnknownFactor;

    public static bool Enough(long uncompressed, long free) => free >= Required(uncompressed);
}

/// <summary>Wo nach 7-Zip gesucht wird. Alles ist austauschbar, damit sich die Suche ohne echtes Windows prüfen lässt.</summary>
public sealed record ToolSearchEnv(
    string? ProgramFiles, string? ProgramFilesX86, string? PathVariable,
    IReadOnlyList<string> ForbiddenDirs, Func<string, bool> FileExists)
{
    /// <summary>Die echte Umgebung. <paramref name="forbidden"/>: Ordner, in denen nie ein Programm gesucht wird (Programm- und Download-Ordner).</summary>
    public static ToolSearchEnv Current(IEnumerable<string> forbidden) => new(
        Environment.GetEnvironmentVariable("ProgramFiles"),
        Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        Environment.GetEnvironmentVariable("PATH"),
        forbidden.Where(f => !string.IsNullOrWhiteSpace(f)).ToList(),
        File.Exists);
}

public static class ToolLocator
{
    /// <summary>
    /// Sucht <c>7z.exe</c>: Pfad aus den Einstellungen (Datei oder Ordner), <c>%ProgramFiles%\7-Zip</c>,
    /// <c>%ProgramFiles(x86)%\7-Zip</c>, dann PATH (dort liegt der Scoop-Shim). Nie in verbotenen Ordnern und nie über einen
    /// relativen Pfad, damit eine Datei im Download- oder Programmordner nicht gestartet wird.
    /// </summary>
    public static string? FindSevenZip(string? configured, ToolSearchEnv env)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string c = configured.Trim().Trim('"');
            if (Accept(c, env)) return Path.GetFullPath(c);
            string inDir = SafeCombine(c, "7z.exe");
            if (inDir.Length > 0 && Accept(inDir, env)) return Path.GetFullPath(inDir);
        }
        foreach (string? pf in new[] { env.ProgramFiles, env.ProgramFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(pf)) continue;
            string path = SafeCombine(pf, "7-Zip", "7z.exe");
            if (path.Length > 0 && Accept(path, env)) return path;
        }
        foreach (string dir in (env.PathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = SafeCombine(dir.Trim().Trim('"'), "7z.exe");
            if (path.Length > 0 && Accept(path, env)) return path;
        }
        return null;
    }

    /// <summary><c>tar.exe</c> aus dem Windows-Systemordner, sonst null. Wird nur dort gesucht.</summary>
    public static string? FindTar(string systemDirectory, Func<string, bool> fileExists)
    {
        string path = Path.Combine(systemDirectory, "tar.exe");
        return fileExists(path) ? path : null;
    }

    private static bool Accept(string path, ToolSearchEnv env)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string forbidden in env.ForbiddenDirs)
            if (IsUnder(full, forbidden)) return false;
        return env.FileExists(full);
    }

    private static string SafeCombine(params string[] parts)
    {
        try
        {
            if (!Path.IsPathFullyQualified(parts[0])) return "";
            return Path.Combine(parts);
        }
        catch (ArgumentException) { return ""; }
    }

    internal static bool IsUnder(string path, string dir)
    {
        try
        {
            string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Liest aus dem Verzeichnis einer ZIP, ob Einträge verschlüsselt sind (Bit 0 der Flags, AES = Methode 99).</summary>
public static class ZipProbe
{
    /// <summary>True, wenn mindestens ein Eintrag verschlüsselt ist. False auch dann, wenn das Verzeichnis nicht zu lesen ist (die Entpackung meldet dann den Fehler).</summary>
    public static bool IsEncrypted(Stream zip)
    {
        try
        {
            long length = zip.Length;
            int tail = (int)Math.Min(length, 22 + 65535);
            var buffer = new byte[tail];
            zip.Seek(length - tail, SeekOrigin.Begin);
            zip.ReadExactly(buffer, 0, tail);

            int eocd = -1;
            for (int i = tail - 22; i >= 0; i--)
                if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 5 && buffer[i + 3] == 6) { eocd = i; break; }
            if (eocd < 0) return false;

            int count = BitConverter.ToUInt16(buffer, eocd + 10);
            uint offset = BitConverter.ToUInt32(buffer, eocd + 16);
            if (offset == 0xFFFFFFFF || count == 0xFFFF) return false; // ZIP64: nicht gelesen

            zip.Seek(offset, SeekOrigin.Begin);
            var header = new byte[46];
            for (int n = 0; n < count; n++)
            {
                zip.ReadExactly(header, 0, 46);
                if (BitConverter.ToUInt32(header, 0) != 0x02014b50) return false;
                ushort flags = BitConverter.ToUInt16(header, 8);
                ushort method = BitConverter.ToUInt16(header, 10);
                if ((flags & 1) != 0 || method == 99) return true;
                int skip = BitConverter.ToUInt16(header, 28) + BitConverter.ToUInt16(header, 30) + BitConverter.ToUInt16(header, 32);
                zip.Seek(skip, SeekOrigin.Current);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }
}
