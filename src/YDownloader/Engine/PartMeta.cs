using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YDownloader.Engine;

/// <summary>Fortschritt neben der .part-Datei, damit ein Abbruch fortgesetzt werden kann.</summary>
public sealed class PartMeta
{
    public string Url { get; set; } = "";
    public long Total { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<SegmentMeta> Segments { get; set; } = new();

    /// <summary>
    /// Die Teildaten dürfen nie verworfen werden (<see cref="DownloadRequest.ProtectPartial"/>). Bleibt gesetzt, auch wenn ein
    /// späterer Aufruf das Flag nicht mehr mitgibt.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Protected { get; set; }
}

public sealed class SegmentMeta
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Done { get; set; }
}

[JsonSerializable(typeof(PartMeta))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class EngineJsonContext : JsonSerializerContext
{
}
