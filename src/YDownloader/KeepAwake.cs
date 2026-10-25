using System.Runtime.InteropServices;

namespace YDownloader;

/// <summary>
/// Hält Windows vom Ruhezustand ab (der Bildschirm darf trotzdem ausgehen). <c>SetThreadExecutionState</c> gilt für den
/// aufrufenden Thread: immer vom UI-Thread aufrufen, der so lange lebt wie das Programm, und beides, An und Aus, dort.
/// </summary>
internal static class KeepAwake
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;

    public static bool IsOn { get; private set; }

    public static void Set(bool on)
    {
        if (on == IsOn) return;
        // Scheitert der Aufruf (Rückgabe 0), bleibt der alte Stand stehen und der nächste Aufruf versucht es noch einmal.
        if (SetThreadExecutionState(on ? Continuous | SystemRequired : Continuous) != 0) IsOn = on;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
