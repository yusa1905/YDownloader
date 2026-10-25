using System.Collections.Generic;

namespace YDownloader.Sheets;

/// <summary>Einträge für die Kategorie-Auswahl. Sonderwerte beginnen mit \u0001 und kollidieren nie mit einem Namen.</summary>
internal static class CategoryChoices
{
    public const string Auto = "\u0001auto";
    public const string Keep = "\u0001keep";

    public static List<(string Label, string? Value)> Build(AppSettings settings, bool auto = false, bool keep = false)
    {
        var list = new List<(string, string?)>();
        if (keep) list.Add((Loc.T("Category.Keep"), Keep));
        if (auto) list.Add((Loc.T("Category.Auto"), Auto));
        list.Add((Loc.T("Category.None"), null));
        if (settings.EffectiveCategories.Count > 0) list.Add(("", "")); // Trennlinie
        foreach (var c in settings.EffectiveCategories) list.Add((c.Name, c.Key));
        return list;
    }
}
