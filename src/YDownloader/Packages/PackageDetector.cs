using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using YDownloader.Engine;

namespace YDownloader.Packages;

/// <summary>Welche Schreibweise für die Teile eines mehrteiligen Archivs gilt. Teile verschiedener Arten bilden nie ein Paket.</summary>
public enum PartKind
{
    /// <summary><c>name.part1.rar</c>, <c>name.part2.rar</c> …</summary>
    RarPart,
    /// <summary><c>name.rar</c>, <c>name.r00</c>, <c>name.r01</c> …</summary>
    RarOld,
    /// <summary><c>name.7z.001</c>, <c>name.7z.002</c> …</summary>
    SevenZip,
    /// <summary><c>name.zip.001</c> … (geteiltes ZIP)</summary>
    ZipSplit,
    /// <summary><c>name.z01</c>, <c>name.z02</c> … und zuletzt <c>name.zip</c></summary>
    ZipOld,
}

/// <summary>Ein Dateiname als Teil eines mehrteiligen Archivs. <see cref="Order"/> ist die Reihenfolge beim Entpacken (0 = erster Teil).</summary>
/// <param name="Base">Name ohne Teil-Endung, wie er in der Datei steht.</param>
/// <param name="Number">Nummer, wie sie im Namen steht (<c>part03</c> = 3, <c>.r00</c> = 0, <c>.z01</c> = 1); -1 beim Teil ohne Nummer (<c>.rar</c>, <c>.zip</c>).</param>
public sealed record PartName(string Base, PartKind Kind, int Number, int Order)
{
    public string Key => Kind + "|" + Base.ToLowerInvariant();
}

/// <summary>
/// Erkennt mehrteilige Archive an ihren Dateinamen und fasst die Teile zu Paketen zusammen. Reine Logik ohne Oberfläche und
/// Netzwerk. Einzelne Dateien (auch ein einzelnes <c>.rar</c> oder <c>.zip</c>) bilden kein Paket: Ein Paket hat mindestens
/// zwei Teile.
/// </summary>
public static class PackageDetector
{
    private static readonly Regex RarPartRx = new(@"^(?<b>.+)\.part(?<n>\d{1,4})\.rar$", Options());
    private static readonly Regex RarOldRx = new(@"^(?<b>.+)\.r(?<n>\d{2,3})$", Options());
    private static readonly Regex SplitRx = new(@"^(?<b>.+)\.(?<e>7z|zip)\.(?<n>\d{3,4})$", Options());
    private static readonly Regex ZipOldRx = new(@"^(?<b>.+)\.z(?<n>\d{2,3})$", Options());
    private static readonly Regex RarRx = new(@"^(?<b>.+)\.rar$", Options());
    private static readonly Regex ZipRx = new(@"^(?<b>.+)\.zip$", Options());

    private static RegexOptions Options() => RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    /// Der Name als Teil eines Archivs, sonst null. <c>name.rar</c> und <c>name.zip</c> werden als erster (RAR) bzw. letzter (ZIP)
    /// Teil gelesen, damit sie mit <c>.r00</c> bzw. <c>.z01</c> zusammenfinden; allein bleiben sie ein einzelnes Archiv.
    /// </summary>
    public static PartName? Parse(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        // .part1.rar vor .rar, sonst wäre "name.part1" die Basis.
        if (RarPartRx.Match(fileName) is { Success: true } m)
        {
            int n = int.Parse(m.Groups["n"].Value);
            return new PartName(m.Groups["b"].Value, PartKind.RarPart, n, n);
        }
        if (SplitRx.Match(fileName) is { Success: true } s)
        {
            int n = int.Parse(s.Groups["n"].Value);
            var kind = s.Groups["e"].Value.Equals("7z", StringComparison.OrdinalIgnoreCase) ? PartKind.SevenZip : PartKind.ZipSplit;
            return new PartName(s.Groups["b"].Value, kind, n, n);
        }
        if (RarOldRx.Match(fileName) is { Success: true } r)
        {
            int n = int.Parse(r.Groups["n"].Value);
            return new PartName(r.Groups["b"].Value, PartKind.RarOld, n, n + 1); // .rar ist Order 0
        }
        if (ZipOldRx.Match(fileName) is { Success: true } z)
        {
            int n = int.Parse(z.Groups["n"].Value);
            return new PartName(z.Groups["b"].Value, PartKind.ZipOld, n, n); // .zip kommt zuletzt
        }
        if (RarRx.Match(fileName) is { Success: true } rar)
            return new PartName(rar.Groups["b"].Value, PartKind.RarOld, -1, 0);
        if (ZipRx.Match(fileName) is { Success: true } zip)
            return new PartName(zip.Groups["b"].Value, PartKind.ZipOld, -1, int.MaxValue);
        return null;
    }

    /// <summary>
    /// Teilt die Dateien in Pakete (mindestens zwei Teile derselben Art und desselben Namens, Groß-/Kleinschreibung egal) und
    /// übrige Dateien. Die Reihenfolge der Eingabe bleibt erhalten: Pakete stehen an der Stelle ihres ersten Teils, innerhalb des
    /// Pakets nach Teil-Reihenfolge. Zwei gleiche Teile (Duplikate) bleiben beide im Paket.
    /// </summary>
    public static IReadOnlyList<PackageGroup<T>> Group<T>(IEnumerable<T> items, Func<T, string> fileName)
    {
        var all = items.Select((item, i) => (item, i, part: Parse(fileName(item)))).ToList();
        var byKey = all.Where(x => x.part is not null)
            .GroupBy(x => x.part!.Key)
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<PackageGroup<T>>();
        var done = new HashSet<string>();
        foreach (var x in all)
        {
            if (x.part is null || !byKey.TryGetValue(x.part.Key, out var members))
            {
                result.Add(new PackageGroup<T>(null, null, new[] { x.item }, Array.Empty<string>()));
                continue;
            }
            if (!done.Add(x.part.Key)) continue;
            var ordered = members.OrderBy(m => m.part!.Order).ThenBy(m => m.i).ToList();
            var first = ordered[0].part!;
            result.Add(new PackageGroup<T>(first.Base, first.Kind, ordered.Select(m => m.item).ToList(),
                Missing(first.Kind, ordered.Select(m => m.part!).ToList())));
        }
        return result;
    }

    /// <summary>
    /// Fehlende Teile als Namensendungen, soweit sie sich aus den vorhandenen ableiten lassen: Lücken in der Zählung, ein fehlender
    /// erster Teil bei <c>.partN.rar</c>, fehlendes <c>.rar</c> bzw. <c>.zip</c>. Fehlt nur der letzte Teil hinten, ist das nicht zu sehen.
    /// </summary>
    private static IReadOnlyList<string> Missing(PartKind kind, List<PartName> parts)
    {
        var missing = new List<string>();
        var numbers = parts.Where(p => p.Number >= 0).Select(p => p.Number).ToHashSet();
        switch (kind)
        {
            case PartKind.RarPart:
                for (int n = 1; n < numbers.Max(); n++)
                    if (!numbers.Contains(n)) missing.Add($"part{n}.rar");
                break;
            case PartKind.SevenZip:
            case PartKind.ZipSplit:
                string ext = kind == PartKind.SevenZip ? "7z" : "zip";
                for (int n = 1; n < numbers.Max(); n++)
                    if (!numbers.Contains(n)) missing.Add($"{ext}.{n:D3}");
                break;
            case PartKind.RarOld:
                if (!parts.Any(p => p.Number < 0)) missing.Add("rar");
                for (int n = 0; n < numbers.DefaultIfEmpty(-1).Max(); n++)
                    if (!numbers.Contains(n)) missing.Add($"r{n:D2}");
                break;
            case PartKind.ZipOld:
                for (int n = 1; n < numbers.DefaultIfEmpty(0).Max(); n++)
                    if (!numbers.Contains(n)) missing.Add($"z{n:D2}");
                if (!parts.Any(p => p.Number < 0)) missing.Add("zip");
                break;
        }
        return missing;
    }

    /// <summary>
    /// Ordnername für ein Paket: der Name ohne Teil-Endung, bereinigt für Windows. Punkte und Unterstriche bleiben, wie sie sind
    /// (der Name soll wiedererkennbar sein), nur Unzulässiges wird ersetzt.
    /// </summary>
    public static string FolderName(string packageName) => FileNames.Sanitize(packageName, "package");
}

/// <summary>
/// Ein Paket (<see cref="Name"/> und <see cref="Kind"/> gesetzt, mindestens zwei Teile) oder eine einzelne Datei (beide Null, genau
/// ein Element).
/// </summary>
/// <param name="Missing">Endungen fehlender Teile, etwa <c>part3.rar</c>. Leer, wenn nichts auffällt.</param>
public sealed record PackageGroup<T>(string? Name, PartKind? Kind, IReadOnlyList<T> Items, IReadOnlyList<string> Missing)
{
    public bool IsPackage => Name is not null;
}
