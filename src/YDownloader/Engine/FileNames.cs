using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace YDownloader.Engine;

/// <summary>Dateinamen aus Server-Angaben ableiten und für Windows bereinigen.</summary>
public static class FileNames
{
    /// <summary>Mit ".part.json.tmp" (14 Zeichen) bleibt der Name unter dem NTFS-Limit von 255 Zeichen.</summary>
    public const int MaxLength = 200;

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Endungen, die einen Seitenaufruf meinen, nicht den Typ der Datei (<c>download.php</c>).</summary>
    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".php", ".asp", ".aspx", ".jsp", ".cgi", ".pl", ".do", ".action", ".html", ".htm", ".shtml", ".xhtml",
    };

    private static readonly string[] NameParameters = { "file", "filename", "name" };

    /// <summary>
    /// Endungen von Archiven, Medien und Programmen: Antwortet der Server für einen solchen Namen mit einer Webseite,
    /// ist das ein Fehler (Fehlerseite, Login, Werbung), keine Datei.
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso",
        ".mkv", ".mp4", ".avi", ".mov", ".webm", ".m4v", ".wmv", ".flv", ".ts",
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic",
        ".exe", ".msi", ".msix", ".appx", ".apk",
    };

    /// <summary>Content-Type → Endung, für Antworten ohne brauchbaren Namen. Nie für octet-stream und text/html.</summary>
    private static readonly Dictionary<string, string> TypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-zip"] = ".zip",
        ["application/x-7z-compressed"] = ".7z",
        ["application/vnd.rar"] = ".rar",
        ["application/x-rar-compressed"] = ".rar",
        ["application/x-rar"] = ".rar",
        ["application/gzip"] = ".gz",
        ["application/x-gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["application/x-bzip2"] = ".bz2",
        ["application/x-xz"] = ".xz",
        ["application/zstd"] = ".zst",
        ["application/x-iso9660-image"] = ".iso",
        ["application/pdf"] = ".pdf",
        ["application/epub+zip"] = ".epub",
        ["application/x-msdownload"] = ".exe",
        ["application/vnd.microsoft.portable-executable"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/vnd.android.package-archive"] = ".apk",
        ["video/mp4"] = ".mp4",
        ["video/x-matroska"] = ".mkv",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
        ["video/x-msvideo"] = ".avi",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/ogg"] = ".ogg",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
    };

    /// <summary>
    /// Name der Datei: <c>Content-Disposition</c> hat Vorrang. Sonst der beste Name aus den Adressen (siehe
    /// <see cref="GuessFromUrls"/>, die endgültige zuerst, dann die ursprüngliche), und fehlt dann noch die Endung, die
    /// aus dem Content-Type. Immer bereinigt.
    /// </summary>
    public static string FromResponse(HttpResponseMessage resp, Uri url, Uri? original = null)
    {
        if (resp.Content.Headers.NonValidated.TryGetValues("Content-Disposition", out var values)
            && FromContentDisposition(values.ToString()) is { } disposition && !string.IsNullOrWhiteSpace(disposition))
            return Sanitize(disposition);

        string? guess = original is null || original == url ? GuessFromUrls(url) : GuessFromUrls(url, original);
        return Sanitize(WithTypeExtension(guess, resp.Content.Headers.ContentType?.MediaType));
    }

    public static string? FromUrl(Uri url)
    {
        string path = url.AbsolutePath;
        string last = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return last.Length > 0 ? last : null;
    }

    /// <summary>Hat der Name eine Endung, die eine Datei meint (nicht <c>.php</c>, <c>.html</c> und Ähnliches)?</summary>
    public static bool HasRealExtension(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string ext = Path.GetExtension(name);
        if (ext.Length is < 2 or > 9 || PageExtensions.Contains(ext)) return false;
        for (int i = 1; i < ext.Length; i++)
            if (!char.IsAsciiLetterOrDigit(ext[i])) return false;
        return true;
    }

    /// <summary>
    /// Wert von <c>file</c>, <c>filename</c> oder <c>name</c> aus der Abfrage, aber nur mit echter Endung
    /// (<c>download.php?file=setup.zip</c> → <c>setup.zip</c>).
    /// </summary>
    public static string? FromQuery(Uri url)
    {
        string query = url.Query;
        if (query.Length < 2) return null;
        foreach (string pair in query[1..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || !NameParameters.Contains(Unescape(pair[..eq]), StringComparer.OrdinalIgnoreCase)) continue;
            string value = Unescape(pair[(eq + 1)..].Replace('+', ' '));
            value = value[(value.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
            if (HasRealExtension(value)) return value;
        }
        return null;
    }

    /// <summary>
    /// Bester Name aus den Adressen, in der Reihenfolge der Angabe: je Adresse der Pfadname mit echter Endung, sonst der Name
    /// aus der Abfrage. Hat keine eine echte Endung, bleibt der erste Pfadname (oder null).
    /// </summary>
    public static string? GuessFromUrls(params Uri[] urls)
    {
        foreach (var url in urls)
        {
            string? path = FromUrl(url);
            if (HasRealExtension(path)) return path;
            if (FromQuery(url) is { } query) return query;
        }
        return urls.Select(FromUrl).FirstOrDefault(n => n is not null);
    }

    /// <summary>Endung für einen Content-Type aus der kleinen Tabelle, sonst null (auch für octet-stream und text/html).</summary>
    public static string? ExtensionFor(string? mediaType) =>
        mediaType is not null && TypeExtensions.TryGetValue(mediaType.Trim(), out var ext) ? ext : null;

    /// <summary>
    /// Fehlt dem Namen eine echte Endung, kommt die aus dem Content-Type dazu (eine Seiten-Endung wie <c>.php</c> weicht ihr).
    /// Ohne Namen wird <c>download</c> daraus. Ist der Typ nicht in der Tabelle, bleibt der Name, wie er ist.
    /// </summary>
    public static string? WithTypeExtension(string? name, string? mediaType)
    {
        if (HasRealExtension(name) || ExtensionFor(mediaType) is not { } ext) return name;
        if (string.IsNullOrWhiteSpace(name)) return "download" + ext;
        string current = Path.GetExtension(name);
        if (PageExtensions.Contains(current)) name = name[..^current.Length];
        return name + ext;
    }

    /// <summary>Der Name endet auf ein Archiv, ein Medium oder ein Programm: Eine Webseite als Antwort kann nicht stimmen.</summary>
    public static bool ExpectsBinary(string? name) =>
        !string.IsNullOrEmpty(name) && BinaryExtensions.Contains(Path.GetExtension(name));

    /// <summary>Der Name endet auf eine Webseite (<c>.html</c>, <c>.htm</c>, <c>.xhtml</c>, <c>.shtml</c>).</summary>
    public static bool IsPageName(string? name) =>
        !string.IsNullOrEmpty(name) && Path.GetExtension(name).ToLowerInvariant() is ".html" or ".htm" or ".xhtml" or ".shtml";

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    /// <summary>
    /// Hoster hängen oft ".html" an den Dateinamen im Link (<c>…/Name.part1.rar.html</c>). Entfernt das, wenn davor noch eine
    /// Endung steht, sonst bleibt der Name unverändert.
    /// </summary>
    public static string StripPageSuffix(string name)
    {
        foreach (string suffix in new[] { ".html", ".htm" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = name[..^suffix.Length];
            if (Path.GetExtension(rest).Length > 1) return rest;
        }
        return name;
    }

    /// <summary>
    /// Liest <c>filename*</c> (RFC 5987) oder <c>filename</c>. Nachsichtig wie Browser: ohne Anführungszeichen mit
    /// Leerzeichen, MIME-kodiert (<c>=?UTF-8?B?…?=</c>) oder roh als UTF-8 gesendet.
    /// </summary>
    public static string? FromContentDisposition(string header)
    {
        string? star = Parameter(header, "filename*");
        if (star is not null)
        {
            // charset'sprache'prozent-kodiert
            int a = star.IndexOf('\'');
            int b = a < 0 ? -1 : star.IndexOf('\'', a + 1);
            if (b > a && TryGetEncoding(star[..a], out var encoding))
                return DecodeHex(star[(b + 1)..], '%', encoding);
        }

        string? plain = Parameter(header, "filename");
        if (plain is null) return null;
        return DecodeMimeWord(plain) ?? Utf8ReadAsLatin1(plain);
    }

    /// <summary>
    /// Macht aus einem Server-Namen einen gültigen Windows-Dateinamen: nur der letzte Pfadteil, keine verbotenen Zeichen
    /// (auch kein ':', das sonst einen Alternate Data Stream anlegt), keine Gerätenamen wie CON oder NUL,
    /// kein Punkt oder Leerzeichen am Ende, höchstens <see cref="MaxLength"/> Zeichen.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;

        // Windows prüft den Teil vor dem ersten Punkt, ohne Leerzeichen am Ende: "nul.txt" und "CON .zip" sind Geräte.
        if (Reserved.Contains(name.Split('.')[0].TrimEnd(' '))) name = "_" + name;

        if (name.Length > MaxLength)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = ""; // keine echte Endung
            int keep = MaxLength - ext.Length;
            if (char.IsHighSurrogate(name[keep - 1])) keep--; // kein halbes Zeichen
            name = name[..keep].TrimEnd('.', ' ') + ext;
        }
        return name;
    }

    /// <summary>Wert eines Parameters. Anführungszeichen und Backslash-Escapes werden aufgelöst.</summary>
    private static string? Parameter(string header, string name)
    {
        int i = 0;
        while (i < header.Length)
        {
            int start = i;
            while (i < header.Length && header[i] != '=' && header[i] != ';') i++;
            string key = header[start..i].Trim();
            if (i >= header.Length || header[i] == ';')
            {
                i++; // Typ wie "attachment" oder Parameter ohne Wert
                continue;
            }
            i++; // '='
            while (i < header.Length && header[i] == ' ') i++;

            string value;
            if (i < header.Length && header[i] == '"')
            {
                var sb = new StringBuilder();
                for (i++; i < header.Length && header[i] != '"'; i++)
                {
                    if (header[i] == '\\' && i + 1 < header.Length) i++;
                    sb.Append(header[i]);
                }
                value = sb.ToString();
                while (i < header.Length && header[i] != ';') i++;
            }
            else
            {
                start = i;
                while (i < header.Length && header[i] != ';') i++;
                value = header[start..i].Trim();
            }
            i++; // ';'
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    /// <summary><c>=?charset?B?base64?=</c> oder <c>=?charset?Q?…?=</c>, sonst null.</summary>
    private static string? DecodeMimeWord(string s)
    {
        var parts = s.Split('?');
        if (parts.Length != 5 || parts[0] != "=" || parts[4] != "=" || !TryGetEncoding(parts[1], out var encoding))
            return null;
        try
        {
            return parts[2].ToUpperInvariant() switch
            {
                "B" => encoding.GetString(Convert.FromBase64String(parts[3])),
                "Q" => DecodeHex(parts[3].Replace('_', ' '), '=', encoding),
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Header-Werte liest .NET als Latin-1. Schickt der Server rohes UTF-8, wird es hier zurückgewandelt.</summary>
    private static string Utf8ReadAsLatin1(string s)
    {
        if (!s.Any(c => c >= 0x80) || s.Any(c => c > 0xFF)) return s;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Encoding.Latin1.GetBytes(s));
        }
        catch (DecoderFallbackException)
        {
            return s; // war echtes Latin-1
        }
    }

    private static string DecodeHex(string s, char marker, Encoding encoding)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == marker && i + 2 < s.Length
                && byte.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
        }
        return encoding.GetString(bytes.ToArray());
    }

    private static bool TryGetEncoding(string name, out Encoding encoding)
    {
        try
        {
            encoding = Encoding.GetEncoding(name.Trim());
            return true;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
            return false;
        }
    }
}
