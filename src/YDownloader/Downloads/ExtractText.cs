using YDownloader.Packages;

namespace YDownloader.Downloads;

/// <summary>Kurze Texte für den Stand des Entpackens, in der Sprache der Oberfläche.</summary>
public static class ExtractText
{
    public static string Describe(ExtractState state, ExtractFailure reason, string? detail) => state switch
    {
        ExtractState.Extracting => Loc.T("Extract.Running"),
        ExtractState.Done => Loc.T("Extract.Done"),
        ExtractState.NeedSevenZip => Loc.T("Extract.NeedSevenZip"),
        ExtractState.NeedPassword => detail is { Length: > 0 } ? Loc.T("Extract.NeedPasswordTried", detail) : Loc.T("Extract.NeedPassword"),
        ExtractState.Failed => Loc.T("Extract.Failed", Reason(reason, detail)),
        _ => "",
    };

    /// <summary>Wie <see cref="Describe"/>, mit „ · “ davor. Leer, wenn es nichts zu sagen gibt.</summary>
    public static string Suffix(ExtractState state, ExtractFailure reason, string? detail) =>
        state == ExtractState.None ? "" : " · " + Describe(state, reason, detail);

    private static string Reason(ExtractFailure reason, string? detail) => reason switch
    {
        ExtractFailure.NoSpace => NoSpace(detail),
        ExtractFailure.UnsafePath => Loc.T("Extract.Reason.UnsafePath"),
        ExtractFailure.Link => Loc.T("Extract.Reason.Link"),
        ExtractFailure.MissingParts => Loc.T("Extract.Reason.MissingParts"),
        ExtractFailure.Unreadable => Loc.T("Extract.Reason.Unreadable"),
        ExtractFailure.Timeout => Loc.T("Extract.Reason.Timeout"),
        ExtractFailure.ToolFailed or ExtractFailure.Io => string.IsNullOrWhiteSpace(detail) ? Loc.T("Extract.Reason.Unknown") : detail,
        _ => Loc.T("Extract.Reason.Unknown"),
    };

    private static string NoSpace(string? detail)
    {
        // „nötig|frei“ in Bytes
        string[] parts = (detail ?? "").Split('|');
        return parts.Length == 2 && long.TryParse(parts[0], out long need) && long.TryParse(parts[1], out long free)
            ? Loc.T("Extract.Reason.NoSpace", Format.Bytes(need), Format.Bytes(free))
            : Loc.T("Extract.Reason.NoSpaceShort");
    }
}
