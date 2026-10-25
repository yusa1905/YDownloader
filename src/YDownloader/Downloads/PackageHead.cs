using System;
using System.Collections.Generic;
using YDownloader.Packages;

namespace YDownloader.Downloads;

/// <summary>Wie der Zustandstext eines Paketkopfs eingefärbt wird (Farben stehen in den Ressourcen).</summary>
public enum PackageTone
{
    None,
    /// <summary>7-Zip oder Passwort nötig.</summary>
    Warning,
    /// <summary>Ein Teil ist gescheitert oder das Entpacken schlug fehl.</summary>
    Error,
}

/// <summary>
/// Kopfzeile eines Pakets in der Hauptliste. Fasst die sichtbaren Teile zusammen: Fortschritt, Tempo, Zustand. Rechnet nur in
/// <see cref="Update"/> (einmal pro Takt, nur für Köpfe mit laufenden Teilen) und meldet nur, was sich geändert hat.
/// Die Teile bleiben eigene Downloads; der Kopf ist reine Ansicht und wird nie gespeichert.
/// </summary>
public sealed class PackageHead : Observable
{
    private List<DownloadItem> _members = new();
    private bool _expanded;

    private string _name = "", _badge = "", _detail = "", _speedText = "", _etaText = "", _state = "";
    private double _fraction;
    private DownloadStatus _status;
    private PackageTone _tone;
    private bool _barHidden;

    public PackageHead(string id) => Id = id;

    public string Id { get; }

    /// <summary>Die sichtbaren Teile in Anzeigereihenfolge.</summary>
    public IReadOnlyList<DownloadItem> Members => _members;

    public bool IsExpanded
    {
        get => _expanded;
        internal set => Set(ref _expanded, value);
    }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Badge { get => _badge; private set => Set(ref _badge, value); }

    /// <summary>„2 von 3 Teilen · 1,2 GB von 3,4 GB · 35 %“.</summary>
    public string DetailText { get => _detail; private set => Set(ref _detail, value); }

    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }

    /// <summary>Entpacken-Zustand oder Fehlerzahl, leer wenn es nichts zu sagen gibt. Steht auch zugeklappt in der Kopfzeile.</summary>
    public string StateText { get => _state; private set => Set(ref _state, value); }

    public PackageTone Tone { get => _tone; private set => Set(ref _tone, value); }

    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    /// <summary>Gesamtzustand: läuft vor wartet vor Fehler vor angehalten; fertig nur, wenn alle Teile fertig sind.</summary>
    public DownloadStatus Status { get => _status; private set => Set(ref _status, value); }

    public bool BarHidden { get => _barHidden; private set => Set(ref _barHidden, value); }

    public bool HasRunning
    {
        get
        {
            foreach (var m in _members)
                if (m.Status == DownloadStatus.Running) return true;
            return false;
        }
    }

    public override string ToString() => _name;

    internal void SetMembers(List<DownloadItem> members, bool expanded)
    {
        _members = members;
        IsExpanded = expanded;
        Update();
    }

    /// <summary>Rechnet alle Werte neu, in einem Durchlauf über die Teile.</summary>
    public void Update()
    {
        int count = _members.Count, done = 0, failed = 0, running = 0, queued = 0, paused = 0;
        long total = 0, loaded = 0, remaining = 0;
        double speed = 0;
        bool anyUnknownSize = false;
        string? packageName = null;
        ExtractState extract = ExtractState.None;
        ExtractFailure reason = ExtractFailure.None;
        string? detail = null, passwordDetail = null;
        bool needSeven = false, needPassword = false, extractFailed = false;

        foreach (var m in _members)
        {
            packageName ??= string.IsNullOrEmpty(m.PackageName) ? null : m.PackageName;
            switch (m.Status)
            {
                case DownloadStatus.Completed: done++; break;
                case DownloadStatus.Failed: failed++; break;
                case DownloadStatus.Running: running++; speed += m.Speed; break;
                case DownloadStatus.Queued: queued++; break;
                default: paused++; break;
            }
            long size = m.TotalBytes;
            if (size > 0)
            {
                total += size;
                long have = m.Status == DownloadStatus.Completed ? size : Math.Min(m.DownloadedBytes, size);
                loaded += have;
                remaining += size - have;
            }
            else anyUnknownSize = true;

            switch (m.Extract)
            {
                case ExtractState.NeedSevenZip: needSeven = true; break;
                case ExtractState.NeedPassword: needPassword = true; passwordDetail ??= m.ExtractDetail; break;
                case ExtractState.Failed: extractFailed = true; break;
            }
            if (m.Extract != ExtractState.None && extract == ExtractState.None)
            {
                extract = m.Extract;
                reason = m.ExtractReason;
                detail = m.ExtractDetail;
            }
        }

        Name = packageName ?? (count > 0 ? _members[0].FileName : Id);
        Badge = count > 0 ? _members[0].Badge : "";

        bool allDone = count > 0 && done == count;
        Status = running > 0 ? DownloadStatus.Running
            : queued > 0 ? DownloadStatus.Queued
            : allDone ? DownloadStatus.Completed
            : failed > 0 ? DownloadStatus.Failed
            : DownloadStatus.Paused;
        Fraction = total > 0 ? Math.Min(1, (double)loaded / total) : 0;
        BarHidden = allDone;

        string parts = allDone
            ? Loc.Plural("Package.Parts", count)
            : Loc.T("Package.PartsOf", done, count);
        string amount = total > 0
            ? (allDone ? Format.Bytes(total) : Loc.T("Item.Amount", Format.Bytes(loaded), Format.Bytes(total), Format.Percent(Fraction)))
            : "";
        DetailText = amount.Length > 0 ? parts + " · " + amount : parts;

        SpeedText = running > 0 && speed >= 1 ? Format.Speed(speed) : "";
        // Nur ehrlich, wenn jede Größe bekannt ist; sonst zeigte die Zeit nur den Rest der bekannten Teile.
        EtaText = running > 0 && speed >= 1 && !anyUnknownSize && remaining > 0
            ? Loc.T("Item.Eta", Format.Duration(remaining / speed))
            : "";

        // Zustandstext: was der Nutzer beheben muss zuerst (Entpacken gescheitert > gescheiterte Teile > 7-Zip > Passwort), dann der Rest.
        if (extractFailed)
        {
            Tone = PackageTone.Error;
            StateText = ExtractText.Describe(ExtractState.Failed, reason, detail);
        }
        else if (failed > 0)
        {
            Tone = PackageTone.Error;
            StateText = Loc.Plural("Stats.Failed", failed);
        }
        else if (needSeven)
        {
            Tone = PackageTone.Warning;
            StateText = ExtractText.Describe(ExtractState.NeedSevenZip, ExtractFailure.None, null);
        }
        else if (needPassword)
        {
            Tone = PackageTone.Warning;
            StateText = ExtractText.Describe(ExtractState.NeedPassword, ExtractFailure.None, passwordDetail);
        }
        else
        {
            Tone = PackageTone.None;
            StateText = extract == ExtractState.None ? "" : ExtractText.Describe(extract, reason, detail);
        }
    }
}
