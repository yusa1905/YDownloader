using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace YDownloader.Downloads;

/// <summary>Findet HTTP(S)-Links in beliebigem Text (Zwischenablage, Drag &amp; Drop, Textdateien).</summary>
public static partial class LinkParser
{
    [GeneratedRegex(@"https?://[^\s""'<>\[\]{}|\\^`]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>Links in Reihenfolge des Auftretens, ohne Satzzeichen am Ende, jeder nur einmal.</summary>
    public static List<string> Extract(string? text) => Extract(text, out _);

    /// <param name="duplicates">Wie viele Links im Text mehrfach vorkamen und übersprungen wurden.</param>
    public static List<string> Extract(string? text, out int duplicates)
    {
        duplicates = 0;
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in UrlPattern().Matches(text))
        {
            string url = TrimEnd(m.Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) continue;
            if (seen.Add(Normalize(uri))) result.Add(url);
            else duplicates++;
        }
        return result;
    }

    public static bool ContainsLink(string? text) => !string.IsNullOrEmpty(text) && UrlPattern().IsMatch(text);

    /// <summary>Schlüssel für die Duplikat-Erkennung: Schema und Host klein, ohne Standardport und ohne #Anker.</summary>
    public static string Normalize(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? Normalize(uri) : url.Trim();

    public static string Normalize(Uri uri)
    {
        string port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return uri.Scheme.ToLowerInvariant() + "://" + uri.IdnHost.ToLowerInvariant() + port + uri.PathAndQuery;
    }

    /// <summary>Schließende Klammern nur abschneiden, wenn sie im Link nicht geöffnet wurden (Wikipedia-Links).</summary>
    private static string TrimEnd(string url)
    {
        while (url.Length > 0)
        {
            char c = url[^1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"') url = url[..^1];
            else if (c == ')' && Count(url, '(') < Count(url, ')')) url = url[..^1];
            else break;
        }
        return url;
    }

    private static int Count(string s, char c)
    {
        int n = 0;
        foreach (char x in s) if (x == c) n++;
        return n;
    }
}
