using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YDownloader.Controls;
using YDownloader.Downloads;

namespace YDownloader.Sheets;

/// <summary>
/// Download bearbeiten: ein Eintrag mit Name, Ordner, Link, Kategorie und Prüfsumme, mehrere nur mit Ordner und Kategorie.
/// Solange ein Download läuft, bleiben Datei und Link gesperrt.
/// </summary>
public partial class EditSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly List<DownloadItem> _items;
    private readonly PopupChoice _category;

    private EditSheet(MainWindow host, DownloadManager downloads, List<DownloadItem> items)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        _items = items;
        var settings = downloads.Settings;
        var first = items[0];

        if (items.Count == 1)
        {
            Heading.Text = first.FileName;
            string size = first.TotalBytes >= 0 ? " · " + Format.Bytes(first.TotalBytes) : "";
            Info.Text = Loc.T("Edit.Info", StatusName(first.Status), size, Format.When(first.AddedAt));
            NameBox.Text = first.FileName;
            FolderBox.Text = first.Folder;
            UrlBox.Text = first.Url;
            ChecksumBox.Text = first.Checksum ?? "";
            // Fertig: die Prüfsumme wirkt erst beim nächsten Laden, dann ist sie hier fehl am Platz.
            ChecksumPanel.Visibility = first.Status == DownloadStatus.Completed ? Visibility.Hidden : Visibility.Visible;

            bool locked = first.Status == DownloadStatus.Running;
            foreach (UIElement e in new UIElement[] { NameBox, FolderBox, BrowseButton, UrlBox, ChecksumBox }) e.IsEnabled = !locked;
            LockHint.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            _category = new PopupChoice(CategoryButton, CategoryChoices.Build(settings), first.Category);
        }
        else
        {
            Heading.Text = Loc.T("Edit.HeadingMany", items.Count);
            Info.Text = Loc.T("Edit.InfoMany");
            NamePanel.Visibility = Visibility.Collapsed;
            UrlPanel.Visibility = Visibility.Collapsed;
            ChecksumPanel.Visibility = Visibility.Hidden;
            string folder = first.Folder;
            FolderBox.Text = items.All(i => string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase)) ? folder : "";
            FolderBox.Tag = Loc.T("Edit.Unchanged");
            if (items.Any(i => i.Status == DownloadStatus.Running))
            {
                LockHint.Text = Loc.T("Edit.LockHintMany");
                LockHint.Visibility = Visibility.Visible;
            }
            _category = new PopupChoice(CategoryButton, CategoryChoices.Build(settings, keep: true), CategoryChoices.Keep);
        }
    }

    public static void Open(MainWindow host, DownloadManager downloads, List<DownloadItem> items)
    {
        if (items.Count == 0) return;
        var sheet = new EditSheet(host, downloads, items);
        host.ShowSheet(sheet, items.Count == 1 && sheet.NameBox.IsEnabled ? sheet.NameBox : sheet.FolderBox);
        if (items.Count == 1)
        {
            // Wie im Finder: nur der Name ohne Endung ist markiert.
            string name = sheet.NameBox.Text;
            int dot = name.LastIndexOf('.');
            sheet.NameBox.Select(0, dot > 0 ? dot : name.Length);
        }
    }

    private static string StatusName(DownloadStatus status) => status switch
    {
        DownloadStatus.Running => Loc.T("Edit.Status.Running"),
        DownloadStatus.Queued => Loc.T("Edit.Status.Queued"),
        DownloadStatus.Completed => Loc.T("Edit.Status.Completed"),
        DownloadStatus.Failed => Loc.T("Edit.Status.Failed"),
        _ => Loc.T("Edit.Status.Paused"),
    };

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var item = _items[0];
        bool partial = item.Status != DownloadStatus.Completed && item.DownloadedBytes > 0;
        UrlHint.Visibility = partial && UrlBox.Text.Trim() != item.Url ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string current = FolderBox.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("Add.ChooseFolder"),
            InitialDirectory = Directory.Exists(current) ? current : _items[0].Folder,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string folder = FolderBox.Text.Trim();
        bool single = _items.Count == 1;
        var item = _items[0];
        bool editFiles = FolderBox.IsEnabled;

        // Erst alles prüfen, dann ändern.
        string? checksum = null;
        string url = UrlBox.Text.Trim();
        string name = NameBox.Text.Trim();
        if (editFiles && folder.Length > 0 && !IsValidFolder(folder)) { ShowError(Loc.T("Edit.Err.InvalidFolder")); return; }
        if (single && editFiles)
        {
            if (folder.Length == 0) { ShowError(Loc.T("Edit.Err.FolderRequired")); return; }
            if (name.Length == 0) { ShowError(Loc.T("Edit.Err.NameRequired")); return; }
            if (!IsHttpUrl(url)) { ShowError(Loc.T("Edit.Err.UrlScheme")); return; }
            if (ChecksumPanel.Visibility == Visibility.Visible && ChecksumBox.Text.Trim().Length > 0)
            {
                if (ChecksumText.Parse(ChecksumBox.Text) is not { } spec)
                {
                    ShowError(Loc.T("Edit.Err.Checksum"));
                    return;
                }
                checksum = ChecksumText.ToText(spec);
            }
        }

        SaveButton.IsEnabled = false;
        string? error = null;
        if (single && editFiles)
        {
            _downloads.SetUrl(item, url);
            if (ChecksumPanel.Visibility == Visibility.Visible) _downloads.SetChecksum(item, checksum);
            error = await _downloads.RelocateAsync(item, folder, name);
        }
        else if (!single && folder.Length > 0)
        {
            foreach (var i in _items.Where(i => i.Status != DownloadStatus.Running))
                error ??= await _downloads.RelocateAsync(i, folder, i.FileName);
        }
        if (_category.Value != CategoryChoices.Keep) _downloads.SetCategory(_items, _category.Value);

        if (error is not null)
        {
            SaveButton.IsEnabled = true;
            ShowError(error);
            return;
        }
        _host.CloseSheet();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static bool IsValidFolder(string folder) =>
        folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(folder);

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
