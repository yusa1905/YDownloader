using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace YDownloader.Downloads;

/// <summary>
/// Macht aus der geordneten, gefilterten Downloadliste die Zeilen der Hauptliste: Gehören mindestens zwei sichtbare Downloads
/// zu einem Paket (<see cref="DownloadItem.PackageId"/>), steht ein <see cref="PackageHead"/> an der Stelle des ersten Teils,
/// darunter (aufgeklappt) alle sichtbaren Teile des Pakets. Ein Paket mit nur einem sichtbaren Teil bleibt eine gewöhnliche Zeile.
/// Reine Logik ohne WPF. Die Reihenfolge der Eingabe (Statusgruppen) bestimmt die Position, hier wird nicht sortiert.
/// Köpfe bleiben über Aufrufe dieselben Objekte, so behält die Liste Auswahl und Scroll-Position.
/// </summary>
public sealed class PackageGrouping
{
    private readonly Dictionary<string, PackageHead> _heads = new();
    private readonly HashSet<string> _expanded = new();

    /// <summary>Die Köpfe der letzten <see cref="Build"/>-Zeilen.</summary>
    public IReadOnlyCollection<PackageHead> Heads => _heads.Values;

    public bool IsExpanded(string packageId) => _expanded.Contains(packageId);

    /// <summary>Klappt auf oder zu. Der Zustand überlebt, auch wenn der Kopf zwischendurch verschwindet (Filter).</summary>
    public void SetExpanded(string packageId, bool expanded)
    {
        if (expanded) _expanded.Add(packageId);
        else _expanded.Remove(packageId);
        if (_heads.TryGetValue(packageId, out var head)) head.IsExpanded = expanded;
    }

    public void Toggle(PackageHead head) => SetExpanded(head.Id, !head.IsExpanded);

    /// <summary>Die Zeilen in Anzeigereihenfolge: <see cref="DownloadItem"/> oder <see cref="PackageHead"/>.</summary>
    public List<object> Build(IReadOnlyList<DownloadItem> ordered)
    {
        var parts = new Dictionary<string, List<DownloadItem>>();
        foreach (var item in ordered)
        {
            if (string.IsNullOrEmpty(item.PackageId)) continue;
            if (!parts.TryGetValue(item.PackageId, out var list)) parts[item.PackageId] = list = new List<DownloadItem>();
            list.Add(item);
        }

        var rows = new List<object>(ordered.Count + parts.Count);
        var seen = new HashSet<string>();
        foreach (var item in ordered)
        {
            string? id = item.PackageId;
            if (string.IsNullOrEmpty(id) || parts[id].Count < 2)
            {
                item.IsPackagePart = false;
                rows.Add(item);
                continue;
            }
            if (!seen.Add(id)) continue; // spätere Teile stehen schon unter dem Kopf

            var members = parts[id];
            if (!_heads.TryGetValue(id, out var head)) _heads[id] = head = new PackageHead(id);
            bool open = _expanded.Contains(id);
            head.SetMembers(members, open);
            rows.Add(head);
            foreach (var m in members)
            {
                m.IsPackagePart = open;
                if (open) rows.Add(m);
            }
        }

        if (_heads.Count != seen.Count)
        {
            foreach (string id in new List<string>(_heads.Keys))
                if (!seen.Contains(id)) _heads.Remove(id);
        }
        return rows;
    }

    /// <summary>Rechnet die Köpfe neu, die laufende Teile haben (der 4-Hz-Takt).</summary>
    public void UpdateRunning()
    {
        foreach (var head in _heads.Values)
            if (head.HasRunning) head.Update();
    }

    /// <summary>Rechnet alle Köpfe neu (Zustandswechsel, Entpacken).</summary>
    public void UpdateAll()
    {
        foreach (var head in _heads.Values) head.Update();
    }

    /// <summary>
    /// Die Downloads hinter einer Auswahl: ein Download ist er selbst, ein Kopf steht für alle seine sichtbaren Teile.
    /// Jeder Download einmal, in der Reihenfolge des ersten Auftretens.
    /// </summary>
    public static List<DownloadItem> Expand(IEnumerable<object> selection)
    {
        var result = new List<DownloadItem>();
        var seen = new HashSet<DownloadItem>(ReferenceComparer<DownloadItem>.Instance);
        foreach (object row in selection)
        {
            switch (row)
            {
                case DownloadItem item:
                    if (seen.Add(item)) result.Add(item);
                    break;
                case PackageHead head:
                    foreach (var m in head.Members)
                        if (seen.Add(m)) result.Add(m);
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Gleicht die Zeilen der Liste mit dem Soll ab, mit möglichst wenigen Einzeländerungen: Vorhandene Zeilen bleiben (und mit
    /// ihnen Auswahl und Scroll-Position), nur Entferntes, Neues und Verschobenes wird gemeldet. Wird der Unterschied groß
    /// (ein Filterwechsel, das erste Befüllen), ersetzt ein einziges Reset alles; das ist schneller als hunderte Meldungen.
    /// </summary>
    public static void Sync(RangeCollection<object> target, IReadOnlyList<object> desired, int resetThreshold = 32)
    {
        var want = new HashSet<object>(desired, ReferenceComparer<object>.Instance);
        int kept = 0;
        foreach (object row in target)
            if (want.Contains(row)) kept++;
        int removed = target.Count - kept;
        int inserted = desired.Count - kept;

        if (target.Count == 0 || removed > resetThreshold || inserted > resetThreshold)
        {
            if (!SameOrder(target, desired)) target.ReplaceAll(desired);
            return;
        }

        for (int i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            int from = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (!ReferenceEquals(target[j], desired[i])) continue;
                from = j;
                break;
            }
            if (from >= 0) target.Move(from, i);
            else target.Insert(i, desired[i]);
        }
    }

    private static bool SameOrder(ObservableCollection<object> target, IReadOnlyList<object> desired)
    {
        if (target.Count != desired.Count) return false;
        for (int i = 0; i < desired.Count; i++)
            if (!ReferenceEquals(target[i], desired[i])) return false;
        return true;
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
