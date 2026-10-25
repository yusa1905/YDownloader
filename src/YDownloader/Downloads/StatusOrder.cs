using System;
using System.Collections.Generic;

namespace YDownloader.Downloads;

/// <summary>Gruppen der Hauptliste, von oben nach unten.</summary>
public enum StatusGroup
{
    /// <summary>Läuft oder wartet.</summary>
    Active = 0,
    /// <summary>Angehalten oder Fehler.</summary>
    Stopped = 1,
    Done = 2,
}

/// <summary>
/// Ordnet die Hauptliste nach Statusgruppe, innerhalb der Gruppe bleibt die Reihenfolge der Quelle. Reine Logik, ohne WPF.
/// Die Gruppen werden bei <see cref="Commit"/> eingefroren: <see cref="Compare"/> vergleicht gegen diesen Stand, nicht gegen
/// den laufenden Status. Die Oberfläche sortiert deshalb nur dann neu, wenn <see cref="IsStale"/> meldet, dass eine Gruppe
/// gewechselt hat (Queued → Running ändert nichts), nie im 4-Hz-Takt.
/// </summary>
public sealed class StatusOrder<T> where T : notnull
{
    private readonly Func<T, StatusGroup> _group;
    private readonly Func<T, int> _indexOf;
    private readonly Dictionary<T, (StatusGroup Group, int Index)> _frozen = new(new RefComparer());

    /// <param name="group">Aktuelle Gruppe eines Eintrags.</param>
    /// <param name="indexOf">Position in der Quelle, für Einträge, die seit dem letzten Commit dazukamen.</param>
    public StatusOrder(Func<T, StatusGroup> group, Func<T, int> indexOf)
    {
        _group = group;
        _indexOf = indexOf;
    }

    /// <summary>Friert Gruppe und Position aller Einträge der Quelle ein.</summary>
    public void Commit(IReadOnlyList<T> source)
    {
        _frozen.Clear();
        for (int i = 0; i < source.Count; i++) _frozen[source[i]] = (_group(source[i]), i);
    }

    /// <summary>
    /// Hat bei mindestens einem Eintrag die Gruppe gewechselt, seit <see cref="Commit"/> zuletzt lief? Einträge, die seitdem
    /// dazukamen, zählen auch (ihre Gruppe beim Einfügen ist nicht eingefroren).
    /// </summary>
    public bool IsStale(IReadOnlyList<T> source)
    {
        foreach (var item in source)
            if (!_frozen.TryGetValue(item, out var f) || f.Group != _group(item)) return true;
        return false;
    }

    /// <summary>Erst nach Gruppe, dann nach Position. Neue Einträge zählen mit ihrer aktuellen Gruppe und Position.</summary>
    public int Compare(T a, T b)
    {
        var (ga, ia) = Frozen(a);
        var (gb, ib) = Frozen(b);
        int g = ga.CompareTo(gb);
        return g != 0 ? g : ia.CompareTo(ib);
    }

    private (StatusGroup Group, int Index) Frozen(T item) =>
        _frozen.TryGetValue(item, out var f) ? f : (_group(item), _indexOf(item));

    /// <summary>Die Quelle in Anzeigereihenfolge (nach einem Commit).</summary>
    public List<T> Arrange(IReadOnlyList<T> source)
    {
        var list = new List<T>(source);
        // Stabil: Gleiche Gruppe heißt gleiche Position der Quelle, also nie unentschieden.
        list.Sort(Compare);
        return list;
    }

    private sealed class RefComparer : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

public static class StatusGroups
{
    public static StatusGroup Of(DownloadStatus status) => status switch
    {
        DownloadStatus.Running or DownloadStatus.Queued => StatusGroup.Active,
        DownloadStatus.Completed => StatusGroup.Done,
        _ => StatusGroup.Stopped,
    };
}
