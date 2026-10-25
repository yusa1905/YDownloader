using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Engine;

/// <summary>
/// Begrenzt, wie viele Verbindungen gleichzeitig zu einem Host offen sind, über alle Downloads hinweg. Wer über dem
/// Limit liegt, wartet in der Reihenfolge des Eintreffens. Das Limit lässt sich im Betrieb ändern, 0 = unbegrenzt.
/// </summary>
public sealed class ConnectionGate
{
    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _limit;
    private int _active;

    public ConnectionGate(int limit = 0) => _limit = Math.Max(0, limit);

    public int Limit
    {
        get { lock (_lock) return _limit; }
        set
        {
            lock (_lock)
            {
                _limit = Math.Max(0, value);
                Admit(); // ein höheres Limit weckt Wartende sofort
            }
        }
    }

    public int Active { get { lock (_lock) return _active; } }
    public int Waiting { get { lock (_lock) return _waiters.Count; } }

    /// <summary>Wartet auf einen freien Platz. Das Ergebnis gibt ihn mit <c>Dispose</c> wieder frei.</summary>
    public ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return ValueTask.FromCanceled<IDisposable>(ct);
        Waiter waiter;
        lock (_lock)
        {
            if (_limit == 0 || _active < _limit)
            {
                _active++;
                return new ValueTask<IDisposable>(new Lease(this));
            }
            waiter = new Waiter();
            waiter.Node = _waiters.AddLast(waiter);
        }
        return WaitAsync(waiter, ct);
    }

    private async ValueTask<IDisposable> WaitAsync(Waiter waiter, CancellationToken ct)
    {
        using var registration = ct.Register(static state =>
        {
            var (gate, w, token) = ((ConnectionGate, Waiter, CancellationToken))state!;
            lock (gate._lock)
            {
                if (w.Node is not null) // noch nicht zugeteilt: aus der Schlange nehmen
                {
                    gate._waiters.Remove(w.Node);
                    w.Node = null;
                    w.Granted.TrySetCanceled(token);
                }
            }
        }, (this, waiter, ct));
        await waiter.Granted.Task.ConfigureAwait(false);
        return new Lease(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            _active--;
            Admit();
        }
    }

    /// <summary>Teilt freie Plätze an die Wartenden zu. Nur unter dem Schloss aufrufen.</summary>
    private void Admit()
    {
        while (_waiters.First is { } node && (_limit == 0 || _active < _limit))
        {
            _waiters.RemoveFirst();
            node.Value.Node = null;
            _active++;
            node.Value.Granted.TrySetResult();
        }
    }

    private sealed class Waiter
    {
        public readonly TaskCompletionSource Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
    }

    private sealed class Lease : IDisposable
    {
        private ConnectionGate? _gate;

        public Lease(ConnectionGate gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
