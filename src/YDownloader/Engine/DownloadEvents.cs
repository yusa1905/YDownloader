using System;

namespace YDownloader.Engine;

/// <summary>
/// Meldungen der Engine, die nicht zum laufenden Fortschritt gehören. Die Aufrufe kommen aus dem Thread-Pool, bei mehreren
/// Segmenten auch gleichzeitig: Implementierungen müssen threadsicher sein und dürfen nicht blockieren.
/// </summary>
public interface IDownloadEvents
{
    /// <summary>Nach der Abfrage beim Server: finale URL, Größe und ob Teilstücke (Resume) möglich sind. Bei einem Neubeginn erneut.</summary>
    void Probed(RemoteFileInfo info);

    /// <summary>
    /// Ein Versuch ist gescheitert, nach <paramref name="delay"/> folgt der nächste. <paramref name="segment"/> ist der Index der
    /// Verbindung, -1 bei Abfrage, Einzelstrom und Neubeginn. <paramref name="attempt"/> zählt die Wiederholungen ab 1.
    /// </summary>
    void Retrying(int segment, int attempt, Exception error, TimeSpan delay);
}
