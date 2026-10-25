using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace YDownloader.Downloads;

/// <summary>Online-Status eines Links für den Filter. Alles, was der Dienst nicht beantwortet hat, ist <see cref="Unknown"/>.</summary>
public enum FilterStatus { Unknown, Online, Offline }

/// <summary>Was der Filter von einem Link wissen darf. Größe -1 = unbekannt, der Host darf fehlen (dann wird er aus dem Link gelesen).</summary>
public readonly record struct FilterItem(string Name, string Url, long Size = -1, string? Host = null,
    FilterStatus Status = FilterStatus.Unknown);

/// <summary>
/// Filter für den Hinzufügen-Dialog (Linkgrabber). Reine Logik ohne Oberfläche.
/// <para>
/// Wörter: Leerzeichen = und, Komma (oder <c>|</c>) = oder, ein <c>-</c> davor verneint (der Link darf dann nicht passen, gilt
/// über alle Alternativen). Ein Wort ist Text (reicht als Teil von Dateiname oder Link, <c>*</c> und <c>?</c> als Platzhalter, dann
/// muss der ganze Name passen) oder eine Angabe: <c>ext:rar</c> (Endung), <c>size&gt;100MB</c>, <c>size&lt;2GB</c> (auch
/// <c>&gt;=</c>, <c>&lt;=</c>, <c>=</c>; Einheiten B, KB, MB, GB, TB zu je 1024; ohne Einheit Bytes), <c>host:rapidgator</c>
/// (Host enthält das Wort), <c>status:online</c>, <c>status:offline</c>, <c>status:unknown</c>. Eine Angabe, die nicht lesbar ist
/// (<c>size&gt;viel</c>, <c>status:egal</c>), gilt als gewöhnlicher Text. Eine Größenangabe passt nie auf einen Link mit unbekannter
/// Größe, auch nicht verneint. Groß- und Kleinschreibung zählt nicht.
/// </para>
/// </summary>
public sealed class LinkFilter
{
    public static readonly LinkFilter All = new(new List<List<Term>>(), new List<Term>());

    private readonly List<List<Term>> _alternatives;
    private readonly List<Term> _excludes;

    private LinkFilter(List<List<Term>> alternatives, List<Term> excludes)
    {
        _alternatives = alternatives;
        _excludes = excludes;
    }

    public bool IsEmpty => _alternatives.Count == 0 && _excludes.Count == 0;

    public static LinkFilter Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return All;
        var alternatives = new List<List<Term>>();
        var excludes = new List<Term>();
        foreach (string part in text.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var terms = new List<Term>();
            foreach (string word in part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 1 && word[0] == '-') excludes.Add(Term.Create(word[1..]));
                else if (word != "-") terms.Add(Term.Create(word));
            }
            if (terms.Count > 0) alternatives.Add(terms);
        }
        return new LinkFilter(alternatives, excludes);
    }

    /// <summary>Nur Name und Link bekannt (Größe unbekannt, Status unbekannt).</summary>
    public bool Matches(string name, string url) => Matches(new FilterItem(name, url));

    public bool Matches(in FilterItem item)
    {
        var host = item.Host ?? HostOf(item.Url);
        var full = item with { Host = host };
        foreach (var term in _excludes)
            if (term.Matches(full)) return false;
        if (_alternatives.Count == 0) return true;
        foreach (var terms in _alternatives)
            if (terms.TrueForAll(t => t.Matches(full))) return true;
        return false;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : "";

    // ---------------------------------------------------------------- Wörter

    private abstract class Term
    {
        public abstract bool Matches(in FilterItem item);

        public static Term Create(string word)
        {
            int colon = word.IndexOf(':');
            if (colon > 0 && colon < word.Length - 1)
            {
                string key = word[..colon].ToLowerInvariant();
                string value = word[(colon + 1)..];
                switch (key)
                {
                    case "ext": return new ExtTerm(value.TrimStart('.'));
                    case "host": return new HostTerm(value);
                    case "status" when ParseStatus(value) is { } status: return new StatusTerm(status);
                }
            }
            if (SizeTerm.TryParse(word, out var size)) return size!;
            return new TextTerm(word);
        }

        private static FilterStatus? ParseStatus(string value) => value.ToLowerInvariant() switch
        {
            "online" => FilterStatus.Online,
            "offline" => FilterStatus.Offline,
            "unknown" => FilterStatus.Unknown,
            _ => null,
        };

        protected static Regex? Glob(string text, bool whole)
        {
            if (!text.Contains('*') && !text.Contains('?')) return null;
            string pattern = Regex.Escape(text).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex(whole ? "^" + pattern + "$" : pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>Gewöhnlicher Text: Teil von Dateiname oder Link, mit Platzhaltern der ganze Name oder Link.</summary>
    private sealed class TextTerm : Term
    {
        private readonly string _text;
        private readonly Regex? _glob;

        public TextTerm(string text)
        {
            _text = text;
            _glob = Glob(text, whole: true);
        }

        public override bool Matches(in FilterItem item) => _glob is not null
            ? _glob.IsMatch(item.Name) || _glob.IsMatch(item.Url)
            : item.Name.Contains(_text, StringComparison.OrdinalIgnoreCase) || item.Url.Contains(_text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>ext:rar</c>: die Endung des Dateinamens ist genau diese (mit Platzhaltern über die ganze Endung).</summary>
    private sealed class ExtTerm : Term
    {
        private readonly string _ext;
        private readonly Regex? _glob;

        public ExtTerm(string ext)
        {
            _ext = ext;
            _glob = Glob(ext, whole: true);
        }

        public override bool Matches(in FilterItem item)
        {
            int dot = item.Name.LastIndexOf('.');
            if (dot < 0 || dot == item.Name.Length - 1) return false;
            ReadOnlySpan<char> ext = item.Name.AsSpan(dot + 1);
            return _glob is not null ? _glob.IsMatch(ext) : ext.Equals(_ext, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary><c>host:rapidgator</c>: der Host des Links enthält das Wort (mit Platzhaltern muss er ganz passen).</summary>
    private sealed class HostTerm : Term
    {
        private readonly string _text;
        private readonly Regex? _glob;

        public HostTerm(string text)
        {
            _text = text;
            _glob = Glob(text, whole: true);
        }

        public override bool Matches(in FilterItem item) => _glob is not null
            ? _glob.IsMatch(item.Host ?? "")
            : (item.Host ?? "").Contains(_text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StatusTerm : Term
    {
        private readonly FilterStatus _status;

        public StatusTerm(FilterStatus status) => _status = status;

        public override bool Matches(in FilterItem item) => item.Status == _status;
    }

    private sealed class SizeTerm : Term
    {
        private static readonly Regex Pattern = new(
            @"^size(?<op>>=|<=|>|<|=|:)(?<n>\d+(?:[.,]\d+)?)(?<unit>[a-z]{0,3})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private readonly string _op;
        private readonly long _bytes;

        private SizeTerm(string op, long bytes)
        {
            _op = op;
            _bytes = bytes;
        }

        public static bool TryParse(string word, out SizeTerm? term)
        {
            term = null;
            if (word.Length > 40 || !word.StartsWith("size", StringComparison.OrdinalIgnoreCase)) return false;
            Match m;
            try
            {
                m = Pattern.Match(word);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            if (!m.Success) return false;
            long factor = m.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "" or "b" => 1L,
                "k" or "kb" or "kib" => 1L << 10,
                "m" or "mb" or "mib" => 1L << 20,
                "g" or "gb" or "gib" => 1L << 30,
                "t" or "tb" or "tib" => 1L << 40,
                _ => 0,
            };
            if (factor == 0 || !double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return false;
            double bytes = n * factor;
            if (bytes > long.MaxValue / 2.0) return false;
            term = new SizeTerm(m.Groups["op"].Value == ":" ? "=" : m.Groups["op"].Value, (long)Math.Round(bytes));
            return true;
        }

        public override bool Matches(in FilterItem item)
        {
            if (item.Size < 0) return false; // unbekannte Größe passt auf keine Größenangabe
            return _op switch
            {
                ">" => item.Size > _bytes,
                ">=" => item.Size >= _bytes,
                "<" => item.Size < _bytes,
                "<=" => item.Size <= _bytes,
                _ => item.Size == _bytes,
            };
        }
    }
}

/// <summary>
/// Gespeicherte Regeln „Automatisch abwählen“: je Zeile ein Filtertext in derselben Schreibweise wie das Filterfeld. Ein Link, auf den
/// irgendeine Regel passt, wird abgewählt. Die Regeln löschen nie etwas, der Benutzer kann jede Zeile wieder anhaken.
/// </summary>
public sealed class DeselectRules
{
    public const int MaxRules = 50;
    public const int MaxRuleLength = 200;

    public static readonly DeselectRules None = new(new List<LinkFilter>());

    private readonly List<LinkFilter> _rules;

    private DeselectRules(List<LinkFilter> rules) => _rules = rules;

    public int Count => _rules.Count;

    public static DeselectRules Parse(IEnumerable<string>? rules)
    {
        var parsed = (rules ?? Array.Empty<string>())
            .Select(r => LinkFilter.Parse(r))
            .Where(f => !f.IsEmpty)
            .Take(MaxRules)
            .ToList();
        return parsed.Count == 0 ? None : new DeselectRules(parsed);
    }

    /// <summary>Eine Regel passt auf den Link.</summary>
    public bool Matches(in FilterItem item)
    {
        foreach (var rule in _rules)
            if (rule.Matches(item)) return true;
        return false;
    }

    /// <summary>
    /// Bringt eingegebene Zeilen in die gespeicherte Form: getrimmt, ohne Leeres und Doppeltes (ohne Rücksicht auf Groß-/Kleinschreibung),
    /// höchstens <see cref="MaxRules"/> Zeilen mit je höchstens <see cref="MaxRuleLength"/> Zeichen.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? lines)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines ?? Array.Empty<string>())
        {
            string rule = line.Trim();
            if (rule.Length > MaxRuleLength) rule = rule[..MaxRuleLength].TrimEnd();
            if (rule.Length > 0 && seen.Add(rule)) result.Add(rule);
            if (result.Count == MaxRules) break;
        }
        return result;
    }
}
