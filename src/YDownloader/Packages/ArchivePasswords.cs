using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using YDownloader.Debrid;

namespace YDownloader.Packages;

/// <summary>
/// Regeln für Archivpasswörter: was ein gültiges Passwort ist, wie die Liste bereinigt wird und wie sie verschlüsselt abgelegt
/// wird. Reine Logik ohne Oberfläche. Ein Passwort steht nie im Klartext in einer Datei, in Logs oder Fehlertexten.
/// </summary>
public static class PasswordRules
{
    /// <summary>Höchstens so viele Passwörter werden bei einem Entpacken nacheinander probiert (und in der Liste gehalten).</summary>
    public const int MaxCandidates = 50;

    public const int MaxLength = 256;

    /// <summary>
    /// Bereinigt eine Eingabe: Leerraum an den Enden fällt weg. Null, wenn nichts übrig bleibt, wenn es zu lang ist oder
    /// Steuerzeichen enthält (Zeilenumbrüche würden bei der Übergabe an 7-Zip ein zweites „Passwort“ erzeugen).
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        string text = raw.Trim();
        if (text.Length == 0 || text.Length > MaxLength) return null;
        foreach (char c in text)
            if (char.IsControl(c)) return null;
        return text;
    }

    /// <summary>Bereinigt, ohne Leeres und Doppeltes (Groß-/Kleinschreibung zählt), höchstens <paramref name="max"/>, Reihenfolge bleibt.</summary>
    public static List<string> Normalize(IEnumerable<string?>? passwords, int max = MaxCandidates)
    {
        var result = new List<string>();
        if (passwords is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? raw in passwords)
        {
            if (Clean(raw) is not { } p || !seen.Add(p)) continue;
            result.Add(p);
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>Ein Passwort je Zeile.</summary>
    public static List<string> ParseLines(string? text) =>
        Normalize((text ?? "").Split('\n', StringSplitOptions.None).Select(l => l.TrimEnd('\r')));

    /// <summary>
    /// Die Reihenfolge der Versuche: erst ohne Passwort (<c>null</c>), dann die eben eingegebenen, dann die Passwörter des Pakets
    /// (etwa aus Click'n'Load), dann die Liste. Doppelte entfallen, insgesamt höchstens <see cref="MaxCandidates"/> Passwörter;
    /// was vorn steht, hat Vorrang, wenn gekürzt werden muss.
    /// </summary>
    public static List<string?> Plan(IEnumerable<string?>? entered, IEnumerable<string?>? package, IEnumerable<string?>? list)
    {
        var all = Normalize((entered ?? Array.Empty<string?>()).Concat(package ?? Array.Empty<string?>()).Concat(list ?? Array.Empty<string?>()));
        var plan = new List<string?>(all.Count + 1) { null };
        plan.AddRange(all);
        return plan;
    }

    // ---------------------------------------------------------------- Ablage (DPAPI)

    private const char Separator = '\n';

    /// <summary>Verschlüsselt die bereinigte Liste (ein Block für alle). Null bei leerer Liste.</summary>
    public static string? Encode(IEnumerable<string?>? passwords)
    {
        var clean = Normalize(passwords);
        return clean.Count == 0 ? null : SecretProtector.Protect(string.Join(Separator, clean));
    }

    /// <summary>Entschlüsselt. Leer, wenn nichts gespeichert ist oder es sich auf diesem Konto nicht lesen lässt.</summary>
    public static List<string> Decode(string? cipher) =>
        SecretProtector.Unprotect(cipher) is { } text ? Normalize(text.Split(Separator)) : new();

    /// <summary>
    /// Ergänzt die Liste um ein Passwort (hinten). Gibt die neue Liste zurück und ob das Passwort darin steht; bei voller Liste
    /// (<see cref="MaxCandidates"/>) kommt es nicht hinzu.
    /// </summary>
    public static (List<string> List, bool Added) Remember(IEnumerable<string> current, string? password)
    {
        var list = Normalize(current);
        if (Clean(password) is not { } p) return (list, false);
        if (list.Contains(p, StringComparer.Ordinal)) return (list, false);
        if (list.Count >= MaxCandidates) return (list, false);
        list.Add(p);
        return (list, true);
    }
}

/// <summary>Passwortliste in <c>settings.json</c>: nur als DPAPI-Chiffre (Base64), nie im Klartext.</summary>
public sealed class ArchivePasswordSettings
{
    /// <summary>Alle Passwörter in einem verschlüsselten Block.</summary>
    public string? ProtectedList { get; set; }

    /// <summary>Die Passwörter im Klartext (nur im Speicher). Leer, wenn keins da ist oder die Chiffre nicht lesbar ist.</summary>
    [JsonIgnore]
    public List<string> Passwords
    {
        get => PasswordRules.Decode(ProtectedList);
        set => ProtectedList = PasswordRules.Encode(value);
    }

    /// <summary>Es ist etwas gespeichert, ob lesbar oder nicht.</summary>
    [JsonIgnore]
    public bool HasList => !string.IsNullOrEmpty(ProtectedList);

    /// <summary>Gespeichert, aber auf diesem Konto nicht lesbar (anderes Windows-Konto, anderer Rechner, beschädigt).</summary>
    [JsonIgnore]
    public bool Unreadable => HasList && SecretProtector.Unprotect(ProtectedList) is null;
}

public enum AttemptOutcome
{
    /// <summary>Es hat geklappt, die Suche endet.</summary>
    Success,
    /// <summary>Das Passwort war falsch oder fehlte, das nächste kommt dran.</summary>
    WrongPassword,
    /// <summary>Etwas anderes ging schief (Platz, Pfad, Programm), weitere Passwörter helfen nicht.</summary>
    Stop,
}

/// <param name="Result">Das Ergebnis, das nach außen geht, wenn dieser Versuch der letzte ist.</param>
public sealed record Attempt(AttemptOutcome Outcome, ExtractResult? Result = null);

/// <param name="Last">Der letzte Versuch.</param>
/// <param name="Password">Das Passwort, mit dem es geklappt hat (null = ohne oder gescheitert).</param>
/// <param name="Tried">Wie viele Passwörter (ohne den Versuch ohne Passwort) probiert wurden.</param>
public sealed record PasswordSearchResult(Attempt Last, string? Password, int Tried);

/// <summary>Probiert die Versuchsfolge der Reihe nach, bis einer klappt oder etwas anderes schiefgeht.</summary>
public static class PasswordSearch
{
    public static PasswordSearchResult Run(IReadOnlyList<string?> plan, Func<string?, Attempt> attempt, CancellationToken ct)
    {
        Attempt last = new(AttemptOutcome.WrongPassword);
        int tried = 0;
        foreach (string? password in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (password is not null)
            {
                if (tried >= PasswordRules.MaxCandidates) break; // nie mehr als das Limit, auch bei einer zu langen Folge
                tried++;
            }
            last = attempt(password);
            if (last.Outcome == AttemptOutcome.Success) return new(last, password, tried);
            if (last.Outcome == AttemptOutcome.Stop) return new(last, null, tried);
        }
        return new(last, null, tried);
    }
}
