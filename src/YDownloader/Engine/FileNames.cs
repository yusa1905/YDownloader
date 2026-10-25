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

    /// <summary>Name aus <c>Content-Disposition</c>, sonst aus dem letzten Pfadteil der URL. Immer bereinigt.</summary>
    public static string FromResponse(HttpResponseMessage resp, Uri url)
    {
        string? name = null;
        if (resp.Content.Headers.NonValidated.TryGetValues("Content-Disposition", out var values))
            name = FromContentDisposition(values.ToString());
        return Sanitize(name ?? FromUrl(url));
    }

    public static string? FromUrl(Uri url)
    {
        string path = url.AbsolutePath;
        string last = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return last.Length > 0 ? last : null;
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
