using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace YDownloader.Engine;

/// <summary>
/// Schreibt eine Datei atomar im Hintergrund, immer nur ein Schreibvorgang zugleich. Kommt während des Schreibens ein neuer
/// Stand, zählt am Ende nur der neueste (ältere werden übersprungen), so kann kein älterer Stand einen neueren überschreiben.
/// </summary>
public sealed class LatestWriter
{
    private readonly Func<string> _path;
    private readonly object _gate = new();
    private byte[]? _pending;
    private List<TaskCompletionSource<Exception?>> _waiters = new();
    private Task _running = Task.CompletedTask;
    private bool _active;

    /// <param name="path">Wird bei jedem Schreiben neu gefragt (der Datenordner kann wechseln).</param>
    public LatestWriter(Func<string> path) => _path = path;

    /// <summary>Nach jedem Schreibvorgang: null = geschafft, sonst der Fehler. Läuft auf einem Hintergrund-Thread.</summary>
    public event Action<Exception?>? Completed;

    /// <summary>Reiht einen Stand ein. Der Task endet mit dem Ergebnis des Schreibens, das diesen oder einen neueren Stand enthielt.</summary>
    public Task<Exception?> Submit(byte[] data)
    {
        var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending = data;
            _waiters.Add(tcs);
            if (!_active)
            {
                _active = true;
                _running = Task.Run(Drain);
            }
        }
        return tcs.Task;
    }

    /// <summary>Wartet, bis alles Eingereichte geschrieben ist (Beenden).</summary>
    public void Flush()
    {
        Task running;
        lock (_gate) running = _running;
        running.Wait();
    }

    private void Drain()
    {
        while (true)
        {
            byte[] data;
            List<TaskCompletionSource<Exception?>> waiters;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _active = false;
                    return;
                }
                data = _pending;
                _pending = null;
                waiters = _waiters;
                _waiters = new();
            }

            Exception? error = null;
            try
            {
                AtomicFile.WriteAllBytes(_path(), data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
            }
            try { Completed?.Invoke(error); }
            catch (Exception) { /* ein fehlerhafter Beobachter darf das Schreiben nicht stoppen */ }
            foreach (var w in waiters) w.TrySetResult(error);
        }
    }
}
