using System;
using System.Collections.Generic;
using System.Linq;
using YDownloader.Engine;
using YDownloader.Packages;

namespace YDownloader.Downloads;

/// <param name="Text">Die passenden Links, einer je Zeile (nicht die ganze Zwischenablage).</param>
public sealed record ClipboardMatch(string Text, int Count);

/// <summary>
/// Prüft Text aus der Zwischenablage nur auf Link-Muster. Ein Link zählt, wenn sein Hoster in der Liste des Debrid-Dienstes
/// steht, wenn er eine bekannte Dateiendung hat (Kategorien) oder wenn er der Teil eines Archivs ist (<c>.part1.rar</c>,
/// <c>.7z.001</c>). Reine Logik, der Inhalt wird weder gespeichert noch geloggt.
/// </summary>
public static class ClipboardScan
{
    /// <summary>Längerer Text wird nicht gelesen (kein Link-Block, eher ein Dokument).</summary>
    public const int MaxChars = 256 * 1024;

    public const int MaxLinks = 200;

    public static ClipboardMatch? Scan(string? text, Func<Uri, bool> isDebridHost, Func<string, bool> isKnownFile)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxChars || !LinkParser.ContainsLink(text)) return null;
        var hits = new List<string>();
        foreach (string link in LinkParser.Extract(text).Take(MaxLinks))
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            string? name = FileNames.FromUrl(uri);
            bool archivePart = name is not null && PackageDetector.Parse(name) is not null;
            if (archivePart || isDebridHost(uri) || (name is not null && isKnownFile(name))) hits.Add(link);
        }
        return hits.Count == 0 ? null : new ClipboardMatch(string.Join(Environment.NewLine, hits), hits.Count);
    }
}

/// <summary>Liest den Text der Zwischenablage. Eine Attrappe ersetzt es in Tests.</summary>
public interface IClipboardReader
{
    string? ReadText();
}

/// <summary>
/// Reagiert auf eine Änderung der Zwischenablage (das Ereignis kommt von Windows, kein Abfragen im Takt): Passen die Links,
/// meldet es einmal einen Hinweis. Kein Hinweis, solange das Fenster im Vordergrund ist (dann hat der Nutzer die Links in der
/// App kopiert), und keiner doppelt für denselben Inhalt.
/// </summary>
public sealed class ClipboardWatcher
{
    private readonly IClipboardReader _reader;
    private readonly Func<Uri, bool> _isDebridHost;
    private readonly Func<string, bool> _isKnownFile;
    private readonly Func<bool> _windowActive;
    private readonly Action<ClipboardMatch> _hint;
    private string? _last; // nur im Speicher, damit derselbe Inhalt nicht zweimal gemeldet wird

    public ClipboardWatcher(IClipboardReader reader, Func<Uri, bool> isDebridHost, Func<string, bool> isKnownFile,
        Func<bool> windowActive, Action<ClipboardMatch> hint)
    {
        _reader = reader;
        _isDebridHost = isDebridHost;
        _isKnownFile = isKnownFile;
        _windowActive = windowActive;
        _hint = hint;
    }

    public void OnClipboardChanged()
    {
        if (_windowActive()) return;
        var match = ClipboardScan.Scan(_reader.ReadText(), _isDebridHost, _isKnownFile);
        if (match is null)
        {
            _last = null; // etwas anderes wurde kopiert: dieselben Links später erneut melden
            return;
        }
        if (match.Text == _last) return;
        _last = match.Text;
        _hint(match);
    }
}
