using System;
using System.Runtime.CompilerServices;
using YDownloader.Browser;

namespace YDownloader;

/// <summary>
/// Einstieg. Startet der Browser die exe als Native-Messaging-Host, läuft nur die kurze Host-Schleife: ohne WPF, ohne Fenster.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (NativeHost.IsHostLaunch(args)) return NativeHost.RunStandard();
        return RunApp();
    }

    // Eigene Methode, damit WPF nur geladen wird, wenn die App wirklich startet.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
