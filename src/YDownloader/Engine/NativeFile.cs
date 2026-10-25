using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace YDownloader.Engine;

/// <summary>Dateisystem-Funktionen, die .NET nicht anbietet: Sparse-Dateien und freier Platz.</summary>
internal static class NativeFile
{
    private const uint FsctlSetSparse = 0x900C4;
    private const int ErrorIoPending = 997;
    public const int ErrorDiskFull = unchecked((int)0x80070070);

    /// <summary>
    /// Setzt oder löscht das Sparse-Flag. False, wenn das Dateisystem das nicht kann (FAT32, exFAT).
    /// Geht auch mit asynchronen Handles: OVERLAPPED mit eigenem Event, dessen unteres Bit verhindert,
    /// dass die Fertigmeldung im Thread-Pool landet.
    /// </summary>
    public static bool TrySetSparse(SafeFileHandle handle, bool sparse)
    {
        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() | 1 };
        byte[] input = { sparse ? (byte)1 : (byte)0 }; // FILE_SET_SPARSE_BUFFER
        if (DeviceIoControl(handle, FsctlSetSparse, input, input.Length, IntPtr.Zero, 0, out _, ref overlapped))
            return true;
        return Marshal.GetLastWin32Error() == ErrorIoPending && GetOverlappedResult(handle, ref overlapped, out _, true);
    }

    /// <summary>Freier Platz für den aktuellen Benutzer (Kontingente eingerechnet), -1 wenn unbekannt.</summary>
    public static long FreeSpace(string directory)
    {
        return GetDiskFreeSpaceEx(directory, out ulong free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : -1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int returned, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, ref NativeOverlapped overlapped, out int bytes,
        bool wait);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total,
        out ulong totalFree);
}
