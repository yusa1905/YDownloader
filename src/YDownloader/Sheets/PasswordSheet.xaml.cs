using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Downloads;
using YDownloader.Packages;

namespace YDownloader.Sheets;

/// <summary>
/// Passwort für ein Paket oder Archiv eingeben. Es gilt für den nächsten Versuch (wird zuerst probiert); mit dem Haken
/// „In Liste merken“ kommt es nach einem Erfolg in die Liste der Einstellungen.
/// </summary>
public partial class PasswordSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly List<DownloadItem> _items;

    private PasswordSheet(MainWindow host, DownloadManager downloads, List<DownloadItem> items)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        _items = items;
        Subject.Text = items.Count == 1 ? SubjectOf(items[0]) : Loc.Plural("Password.Subjects", items.Count);
    }

    private static string SubjectOf(DownloadItem item) => string.IsNullOrEmpty(item.PackageName) ? item.FileName : item.PackageName;

    /// <summary>Öffnet das Sheet für die markierten Downloads, die ein Passwort annehmen können. Ohne solche tut es nichts.</summary>
    public static void Open(MainWindow host, DownloadManager downloads, IEnumerable<DownloadItem> selected)
    {
        var items = selected.Where(downloads.CanEnterPassword).ToList();
        if (items.Count == 0 || host.IsSheetOpen) return;
        var sheet = new PasswordSheet(host, downloads, items);
        host.ShowSheet(sheet, sheet.PasswordInput);
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => Ok.IsEnabled = PasswordRules.Clean(PasswordInput.Password) is not null;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string password = PasswordInput.Password;
        bool remember = RememberBox.IsChecked == true;
        PasswordInput.Clear();
        _host.CloseSheet();
        _downloads.EnterPassword(_items, password, remember);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        _host.CloseSheet();
    }
}
