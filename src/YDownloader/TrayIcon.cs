using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace YDownloader;

/// <summary>
/// Tray-Symbol mit Öffnen/Beenden, direkt über Shell_NotifyIcon (WinForms kostete ca. 10 MB Working Set).
/// Wird erst nach dem ersten Fenster angelegt. Rückmeldungen kommen an ein eigenes unsichtbares Fenster,
/// damit ein Rechtsklick das Hauptfenster nicht nach vorn holt.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string ClassName = "YDownloaderTray";
    private const string ShowMessageName = "YDownloader.Show";
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int CmdOpen = 1, CmdExit = 2;

    private readonly Window _window;
    private readonly WndProc _proc; // hält den Delegaten am Leben, solange das Fenster existiert
    private readonly IntPtr _hwnd;
    private readonly IntPtr _module;
    private IntPtr _icon;
    private readonly uint _taskbarCreated;
    private readonly uint _showMessage;

    public TrayIcon(Window window)
    {
        _window = window;
        _proc = OnMessage;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _showMessage = RegisterWindowMessage(ShowMessageName);
        IntPtr module = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = module,
            ClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        // Fenster der obersten Ebene statt HWND_MESSAGE: Nur solche bekommen "TaskbarCreated" (Explorer neu gestartet).
        _hwnd = CreateWindowEx(WsExToolWindow, ClassName, "YDownloader", WsPopup, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        _module = module;
        _icon = LoadSmallIcon();
        Add();
    }

    /// <summary>Das kleine Symbol in der Größe, die das System bei der DPI dieses Monitors verlangt (16 px bei 100 %, 24 px bei 150 %).</summary>
    private IntPtr LoadSmallIcon()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        return LoadImage(_module, (IntPtr)IdiApplication, ImageIcon,
            GetSystemMetricsForDpi(SmCxSmIcon, dpi), GetSystemMetricsForDpi(SmCySmIcon, dpi), LrShared);
    }

    private void RefreshIcon()
    {
        _icon = LoadSmallIcon();
        if (!IsAdded) return;
        var data = Data(NifIcon);
        Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>Das Symbol steht im Infobereich. Nur dann taugt das Tray als Ort für ein ausgeblendetes Fenster.</summary>
    public bool IsAdded { get; private set; }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        IsAdded = Shell_NotifyIcon(NimAdd, ref data);
        if (!IsAdded) return; // Explorer läuft nicht: kommt mit "TaskbarCreated" nach
        data.Version = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    /// <summary>
    /// Meldung im Infobereich (unter Windows 11 als Benachrichtigung). Ein Klick darauf öffnet das Fenster oder führt
    /// <paramref name="onClick"/> aus, wenn es gegeben ist.
    /// </summary>
    public void Notify(string title, string text, bool error, Action? onClick = null)
    {
        _onBalloonClick = onClick; // die letzte Meldung gilt, auch ohne Aktion
        if (!IsAdded) return;
        var data = Data(NifInfo);
        data.InfoTitle = Clip(title, 63);
        data.Info = Clip(text, 255);
        data.InfoFlags = error ? NiifError : NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private Action? _onBalloonClick;

    /// <summary>
    /// Schaltet die Windows-Meldung „Zwischenablage geändert“ (WM_CLIPBOARDUPDATE) für das Tray-Fenster ein oder aus. Sie kommt
    /// nur bei einer Änderung, es wird nichts im Takt abgefragt.
    /// </summary>
    public bool WatchClipboard
    {
        get => _watching;
        set
        {
            if (value == _watching) return;
            _watching = value ? AddClipboardFormatListener(_hwnd) : !RemoveClipboardFormatListener(_hwnd);
        }
    }

    private bool _watching;

    /// <summary>Die Zwischenablage hat sich geändert (nur solange <see cref="WatchClipboard"/> an ist). Kommt im UI-Thread an.</summary>
    public event Action? ClipboardChanged;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Bittet eine schon laufende Instanz, ihr Fenster zu zeigen. Sie hört auf dieselbe Nachricht wie das Tray-Symbol.</summary>
    public static void ShowRunningInstance()
    {
        IntPtr hwnd = FindWindow(ClassName, null);
        if (hwnd == IntPtr.Zero) return;
        // Der neue Start hat gerade den Vordergrund: weitergeben, sonst blinkt die andere Instanz nur in der Taskleiste.
        GetWindowThreadProcessId(hwnd, out uint pid);
        AllowSetForegroundWindow(pid);
        PostMessage(hwnd, RegisterWindowMessage(ShowMessageName), IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Gibt Text mit Links an eine laufende Instanz (Erweiterung über den Native-Messaging-Host). Leerer Text fragt nur, ob sie
    /// läuft. <c>false</c>, wenn keine läuft oder sie nicht antwortet.
    /// </summary>
    public static bool SendLinks(string text)
    {
        IntPtr hwnd = FindWindow(ClassName, null);
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        AllowSetForegroundWindow(pid);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        IntPtr buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var data = new CopyData { Id = LinksMagic, Size = (uint)bytes.Length, Data = buffer };
            IntPtr result = SendMessageTimeout(hwnd, WmCopyData, IntPtr.Zero, ref data, SmtoAbortIfHung, 3000, out IntPtr answer);
            return result != IntPtr.Zero && answer != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Links, die ein anderer Prozess (der Native-Messaging-Host) geschickt hat. Kommt im UI-Thread an.</summary>
    public event Action<string>? LinksReceived;

    private IntPtr ReceiveLinks(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<CopyData>(lParam);
        if (data.Id != LinksMagic || data.Size > MaxLinksBytes) return IntPtr.Zero; // fremde Nachricht
        if (data.Size == 0) return (IntPtr)1; // nur eine Anfrage, ob die App läuft
        var bytes = new byte[data.Size];
        Marshal.Copy(data.Data, bytes, 0, bytes.Length);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        _window.Dispatcher.BeginInvoke(new Action(() => LinksReceived?.Invoke(text)));
        return (IntPtr)1;
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "YDownloader",
    };

    // Läuft als nativer Rückruf: Arbeit nur über den Dispatcher, damit Ausnahmen den normalen WPF-Weg nehmen.
    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            // Version 4: Ereignis im unteren Wort von lParam, Ankerpunkt (Bildschirm) in wParam.
            int ev = (int)((long)lParam & 0xFFFF);
            long anchor = (long)wParam;
            if (ev == NinBalloonUserClick && _onBalloonClick is { } onClick)
            {
                _onBalloonClick = null;
                _window.Dispatcher.BeginInvoke(onClick);
            }
            else if (ev == NinSelect || ev == NinKeySelect || ev == NinBalloonUserClick)
                _window.Dispatcher.BeginInvoke(new Action(Restore));
            else if (ev == WmContextMenu)
                _window.Dispatcher.BeginInvoke(new Action(() => ShowMenu((short)anchor, (short)(anchor >> 16))));
            return IntPtr.Zero;
        }
        if (msg == WmCopyData) return ReceiveLinks(lParam);
        if (msg == WmClipboardUpdate)
        {
            _window.Dispatcher.BeginInvoke(new Action(() => ClipboardChanged?.Invoke()));
            return IntPtr.Zero;
        }
        if (msg == WmDpiChanged) _window.Dispatcher.BeginInvoke(new Action(RefreshIcon));
        else if (msg == _taskbarCreated) { _icon = LoadSmallIcon(); Add(); }
        else if (msg == _showMessage) _window.Dispatcher.BeginInvoke(new Action(Restore));
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString, CmdOpen, Loc.T("Tray.Open"));
            AppendMenu(menu, MfString, CmdExit, Loc.T("Tray.Exit"));
            SetMenuDefaultItem(menu, CmdOpen, 0);
            // Ohne Vordergrund schließt das Menü beim Klick daneben nicht, WM_NULL danach gehört dazu.
            SetForegroundWindow(_hwnd);
            uint align = GetSystemMetrics(SmMenuDropAlignment) != 0 ? TpmRightAlign : TpmLeftAlign;
            int cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmNoNotify | TpmRightButton | TpmBottomAlign | align,
                x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (cmd == CmdOpen) Restore();
            else if (cmd == CmdExit) ((App)Application.Current).Quit();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Restore()
    {
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    public void Dispose()
    {
        WatchClipboard = false;
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        DestroyWindow(_hwnd);
        UnregisterClass(ClassName, GetModuleHandle(null));
    }

    // ---------------------------------------------------------------- Win32

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifInfo = 1, NiifError = 3;
    private const uint NotifyIconVersion4 = 4;
    private const int NinSelect = 0x400, NinKeySelect = 0x401, NinBalloonUserClick = 0x405, WmContextMenu = 0x7B;
    private const uint WmNull = 0, WmDpiChanged = 0x02E0;
    private const uint WsPopup = 0x80000000, WsExToolWindow = 0x80;
    private const int IdiApplication = 32512;
    private const uint ImageIcon = 1, LrShared = 0x8000;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50, SmMenuDropAlignment = 40;
    private const uint MfString = 0;
    private const uint TpmLeftAlign = 0, TpmRightButton = 0x2, TpmRightAlign = 0x8, TpmBottomAlign = 0x20,
        TpmNoNotify = 0x80, TpmReturnCmd = 0x100;

    private const uint WmCopyData = 0x4A, SmtoAbortIfHung = 0x2, WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    private const nuint LinksMagic = 0x59444C31; // "YDL1": nur eigene Nachrichten annehmen
    private const uint MaxLinksBytes = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nuint Id;
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, ref CopyData lParam,
        uint flags, uint timeout, out IntPtr result);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version; // Union mit uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, int item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
