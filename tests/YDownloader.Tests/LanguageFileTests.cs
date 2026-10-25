using System.Collections.Generic;
using System.Linq;
using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace YDownloader.Tests;

/// <summary>Jede Sprachdatei hat genau die Schlüssel und Platzhalter von en.json (liest nur die eingebetteten Dateien).</summary>
public class LanguageFileTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.Compiled);

    public static IEnumerable<object[]> Tags() => Loc.Languages.Select(l => new object[] { l.Tag });

    private static Dictionary<string, string> Load(string tag)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Lang." + tag + ".json");
        Assert.NotNull(stream);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(stream!, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        foreach (var property in doc.RootElement.EnumerateObject())
            map.Add(property.Name, property.Value.GetString()!);
        return map;
    }

    private static string Slots(string text) =>
        string.Join(",", Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s, StringComparer.Ordinal));

    [Fact]
    public void Every_language_has_a_file_and_a_name()
    {
        Assert.Contains(Loc.Languages, l => l.Tag == Loc.FallbackTag);
        Assert.Equal(Loc.Languages.Length, Loc.Languages.Select(l => l.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Loc.Languages, l => Assert.False(string.IsNullOrWhiteSpace(l.Name)));
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public void Keys_and_placeholders_match_English(string tag)
    {
        var english = Load("en");
        var other = Load(tag);

        Assert.Empty(english.Keys.Except(other.Keys).OrderBy(k => k));
        Assert.Empty(other.Keys.Except(english.Keys).OrderBy(k => k));

        var mismatches = english
            .Where(pair => other.TryGetValue(pair.Key, out string? text) && Slots(text) != Slots(pair.Value))
            .Select(pair => pair.Key + ": " + Slots(pair.Value) + " <> " + Slots(other[pair.Key]))
            .ToList();
        Assert.Empty(mismatches);
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public void Texts_are_not_empty_except_format_separators(string tag)
    {
        var empty = Load(tag).Where(p => p.Value.Length == 0).Select(p => p.Key).ToList();
        Assert.Empty(empty);
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public void Language_tag_is_found_by_resolve(string tag)
    {
        Assert.Equal(tag, Loc.Resolve(new[] { tag }, Loc.Languages));
    }
}
