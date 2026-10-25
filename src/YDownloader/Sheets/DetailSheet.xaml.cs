using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YDownloader.Downloads;

namespace YDownloader.Sheets;

/// <summary>
/// Alle Angaben zu einem Download: Kopf (Link, finale Adresse, Server, Ziel, Größe, Resume), jede Verbindung mit eigenem
/// Graphen und der Verlauf (Start, Ende, Dauer, Durchschnitt, Wiederholungen, Fehler, Prüfsumme). Bei einem laufenden
/// Download aktualisiert sich alles im 4-Hz-Takt des <see cref="DownloadManager"/>, die Ansicht hat keinen eigenen Timer.
/// </summary>
public partial class DetailSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadDetail _detail;

    private DetailSheet(MainWindow host, DownloadItem item)
    {
        InitializeComponent();
        _host = host;
        _detail = new DownloadDetail(item);
        DataContext = _detail;
        Unloaded += (_, _) => _detail.Close();
    }

    public static void Open(MainWindow host, DownloadItem item)
    {
        if (host.IsSheetOpen) return;
        var sheet = new DetailSheet(host, item);
        host.ShowSheet(sheet, sheet.CloseButton);
    }

    /// <summary>
    /// Ein markierbares Textfeld frisst das Mausrad, auch wenn es selbst nicht scrollt. Das Rad soll die Ansicht bewegen.
    /// </summary>
    private void Text_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset - e.Delta * 0.4);
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
