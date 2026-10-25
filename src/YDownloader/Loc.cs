using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace YDownloader;

/// <summary>Eine Sprache der Oberfläche: Kennung (etwa <c>de</c> oder <c>pt-BR</c>) und ihr Name in ihr selbst.</summary>
public sealed record LanguageInfo(string Tag, string Name);

/// <summary>
/// Alle sichtbaren Texte kommen aus Sprachdateien: <c>Languages/*.json</c>, in die exe eingebettet, je Sprache eine flache
/// Tabelle Schlüssel → Text. Platzhalter sind <c>{0}</c>, <c>{1}</c> …, damit eine Sprache die Reihenfolge ändern kann.
/// Gelesen wird nur die gewählte Sprache; Englisch erst, wenn dort ein Schlüssel fehlt.
/// Die Sprache steht nach dem Start fest (eine Änderung gilt nach dem Neustart), die Tabellen sind danach unveränderlich
/// und von jedem Thread aus lesbar.
/// <para>
/// Eine neue Sprache braucht eine Datei <c>Languages/xx.json</c> (mit Eintrag in <see cref="Languages"/>) und sonst nichts.
/// Zahlen und Datum richten sich nach den Schlüsseln <c>Format.*</c> der Datei, die App läuft im Invariant-Modus.
/// </para>
/// </summary>
public static class Loc
{
    public const string FallbackTag = "en";

    /// <summary>Die vorhandenen Sprachen, in der Reihenfolge der Auswahl. Weitere vor dem ersten Release: pl, ru, zh-Hans, ja, ko.</summary>
    public static readonly LanguageInfo[] Languages =
    {
        new("de", "Deutsch"),
        new("en", "English"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("pt-BR", "Português (Brasil)"),
        new("tr", "Türkçe"),
    };

    private static readonly object Gate = new();
    private static volatile Dictionary<string, string>? _texts;
    private static Dictionary<string, string>? _fallback;
    private static Task<string?>? _pending;

    /// <summary>Kennung der Sprache, in der die Oberfläche läuft.</summary>
    public static string Language { get; private set; } = FallbackTag;

    /// <summary>Zahlenformat der Sprache (Dezimal- und Tausendertrenner aus der Sprachdatei).</summary>
    public static NumberFormatInfo Number { get; private set; } = new();

    /// <summary>
    /// Die Wahl aus <c>settings.json</c> wird im Hintergrund gelesen: Der erste Text, der gebraucht wird, wartet darauf.
    /// Das Lesen ist meist fertig, bevor das Fenster seine Texte braucht.
    /// </summary>
    public static void BeginInit(Task<string?> setting) => _pending = setting;

    /// <summary>
    /// Legt die Sprache fest. <paramref name="setting"/> ist die Wahl des Nutzers (Null oder leer = der Sprache von Windows folgen,
    /// gibt es sie nicht, gilt Englisch).
    /// </summary>
    public static void Init(string? setting)
    {
        lock (Gate)
        {
            string tag = Find(setting) ?? Resolve(WindowsLanguages(), Languages) ?? FallbackTag;
            var texts = Read(tag);
            Number = new NumberFormatInfo
            {
                NumberDecimalSeparator = Pick(texts, "Format.Decimal", "."),
                NumberGroupSeparator = Pick(texts, "Format.Group", ","),
            };
            Language = tag;
            _texts = texts;
        }
    }

    private static Dictionary<string, string> Texts
    {
        get
        {
            var texts = _texts;
            if (texts is not null) return texts;
            lock (Gate)
            {
                if (_texts is null) Init(Pending());
                return _texts!;
            }
        }
    }

    /// <summary>Die Wahl aus <see cref="BeginInit"/>. Scheitert das Lesen der Einstellungen, folgt die Sprache Windows.</summary>
    private static string? Pending()
    {
        try { return _pending?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Der Text zum Schlüssel. Fehlt er in der Sprache, kommt der englische, fehlt er auch dort, der Schlüssel in Klammern.</summary>
    public static string T(string key) =>
        Texts.TryGetValue(key, out string? text) ? text : FromFallback(key);

    /// <summary>Wie <see cref="T(string)"/>, mit den Platzhaltern <c>{0}</c>, <c>{1}</c> … ersetzt (Zahlen im Format der Sprache).</summary>
    public static string T(string key, params object?[] args) => string.Format(Number, T(key), args);

    /// <summary>
    /// Text mit Einzahl und Mehrzahl: <c>key.one</c> bei genau 1, sonst <c>key.other</c>. <c>{0}</c> ist die Zahl, danach folgen
    /// <paramref name="more"/>. Sprachen mit weiteren Formen brauchen hier weitere Kategorien (zwei bis fünf Zeilen).
    /// </summary>
    public static string Plural(string key, long n, params object?[] more)
    {
        var args = new object?[more.Length + 1];
        args[0] = n;
        more.CopyTo(args, 1);
        return T(key + (n == 1 ? ".one" : ".other"), args);
    }

    private static string FromFallback(string key)
    {
        if (Language != FallbackTag)
        {
            Dictionary<string, string> map;
            lock (Gate) map = _fallback ??= Read(FallbackTag);
            if (map.TryGetValue(key, out string? text)) return text;
        }
        return "[" + key + "]";
    }

    /// <summary>
    /// Der Text zum Schlüssel in jeder vorhandenen Sprache (ohne Doppelte). Nur für die Migration alter Daten, liest die
    /// Dateien bei Bedarf und berührt die gewählte Sprache nicht; von jedem Thread aus aufrufbar.
    /// </summary>
    public static IReadOnlyList<string> TextsInAllLanguages(string key)
    {
        var result = new List<string>();
        lock (Gate)
        {
            foreach (var language in Languages)
            {
                _all ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                if (!_all.TryGetValue(language.Tag, out var map)) _all[language.Tag] = map = Read(language.Tag);
                if (map.TryGetValue(key, out string? text) && !result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>>? _all;

    private static string? Find(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        foreach (var language in Languages)
            if (string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase)) return language.Tag;
        return null;
    }

    private static string Pick(Dictionary<string, string> texts, string key, string fallback) =>
        texts.TryGetValue(key, out string? value) && value.Length > 0 ? value : fallback;

    // ---------------------------------------------------------------- Datei

    private static Dictionary<string, string> Read(string tag)
    {
        var map = new Dictionary<string, string>(1024, StringComparer.Ordinal);
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Lang." + tag + ".json");
        if (stream is null) return map;
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0; // BOM
        var reader = new Utf8JsonReader(data.AsSpan(start),
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string? key = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName) key = reader.GetString();
            else if (reader.TokenType == JsonTokenType.String && key is not null)
            {
                map[key] = reader.GetString()!;
                key = null;
            }
        }
        return map;
    }

    // ---------------------------------------------------------------- Sprache von Windows

    /// <summary>
    /// Die erste Sprache aus <paramref name="preferred"/> (Reihenfolge der Windows-Einstellung), für die es eine Datei gibt:
    /// erst genau (<c>pt-BR</c>), dann als Obergruppe (<c>de-DE</c> findet <c>de</c>, <c>zh-Hans-CN</c> findet <c>zh-Hans</c>),
    /// dann über die Hauptsprache (<c>pt-PT</c> findet <c>pt-BR</c>). Chinesisch nur mit passender Schrift. Null, wenn nichts passt.
    /// </summary>
    internal static string? Resolve(IReadOnlyList<string> preferred, IReadOnlyList<LanguageInfo> available)
    {
        foreach (string raw in preferred)
        {
            string wanted = AddChineseScript(raw);
            foreach (var language in available)
                if (string.Equals(language.Tag, wanted, StringComparison.OrdinalIgnoreCase)) return language.Tag;
            foreach (var language in available)
                if (wanted.StartsWith(language.Tag + "-", StringComparison.OrdinalIgnoreCase)) return language.Tag;
            string primary = Primary(wanted);
            if (primary.Equals("zh", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var language in available)
                if (Primary(language.Tag).Equals(primary, StringComparison.OrdinalIgnoreCase)) return language.Tag;
        }
        return null;
    }

    /// <summary>Windows nennt Chinesisch nach Region (<c>zh-CN</c>), die Dateien nach Schrift (<c>zh-Hans</c>): <c>zh-CN</c> wird <c>zh-Hans-CN</c>.</summary>
    private static string AddChineseScript(string tag)
    {
        if (!tag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)) return tag;
        string[] parts = tag.Split('-');
        if (parts.Any(p => p.Equals("Hans", StringComparison.OrdinalIgnoreCase) || p.Equals("Hant", StringComparison.OrdinalIgnoreCase))) return tag;
        string region = parts[^1];
        if (region.Equals("CN", StringComparison.OrdinalIgnoreCase) || region.Equals("SG", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans-" + region;
        if (region.Equals("TW", StringComparison.OrdinalIgnoreCase) || region.Equals("HK", StringComparison.OrdinalIgnoreCase)
            || region.Equals("MO", StringComparison.OrdinalIgnoreCase))
            return "zh-Hant-" + region;
        return tag;
    }

    private static string Primary(string tag)
    {
        int dash = tag.IndexOf('-');
        return dash < 0 ? tag : tag[..dash];
    }

    /// <summary>
    /// Die Anzeigesprachen des Benutzers laut Windows, die wichtigste zuerst. Direkt bei Windows gefragt, weil
    /// <c>CultureInfo</c> im Invariant-Modus nichts liefert. Leer, wenn die Abfrage scheitert.
    /// </summary>
    private static string[] WindowsLanguages()
    {
        try
        {
            const uint MuiLanguageName = 0x8;
            uint count = 0, size = 0;
            if (!GetUserPreferredUILanguages(MuiLanguageName, out count, null, ref size) || size == 0) return Array.Empty<string>();
            var buffer = new char[size];
            if (!GetUserPreferredUILanguages(MuiLanguageName, out count, buffer, ref size)) return Array.Empty<string>();
            // Doppelt abgeschlossene Liste: "de-DE\0en-US\0\0".
            return new string(buffer, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return Array.Empty<string>();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, [Out] char[]? buffer, ref uint size);
}
