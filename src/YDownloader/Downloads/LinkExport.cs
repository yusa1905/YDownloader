using System.Collections.Generic;
using System.Linq;
using System.Text;
using YDownloader.Engine;

namespace YDownloader.Downloads;

/// <summary>Export der Links als Textdatei: ein Link je Zeile. Die Oberfläche fragt nur nach dem Pfad, alles andere steht hier.</summary>
public static class LinkExport
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Ein Link je Zeile (CRLF, auch nach der letzten), jeder nur einmal in der Reihenfolge der Liste, Leeres fällt weg.</summary>
    public static string BuildText(IEnumerable<string> urls)
    {
        var text = new StringBuilder();
        foreach (string url in urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(System.StringComparer.Ordinal))
            text.Append(url).Append("\r\n");
        return text.ToString();
    }

    /// <summary>Vorschlag für den Dateinamen: der Paketname, wenn alle Downloads zum selben Paket gehören, sonst <c>links.txt</c>.</summary>
    public static string SuggestedName(IReadOnlyList<DownloadItem> items)
    {
        string? package = items.Count > 0 && items.All(i => !string.IsNullOrWhiteSpace(i.PackageName) && i.PackageName == items[0].PackageName)
            ? items[0].PackageName
            : null;
        return package is null ? "links.txt" : FileNames.Sanitize(package + ".txt", "links.txt");
    }

    /// <summary>Schreibt die Links als UTF-8 ohne BOM, atomar (temporäre Datei, dann ersetzen).</summary>
    public static void Write(string path, IEnumerable<string> urls) =>
        AtomicFile.WriteAllBytes(path, Utf8.GetBytes(BuildText(urls)));
}
