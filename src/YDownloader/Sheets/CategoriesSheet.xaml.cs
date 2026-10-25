using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YDownloader.Downloads;

namespace YDownloader.Sheets;

/// <summary>
/// Eine bearbeitbare Zeile. <see cref="OriginalKey"/> erkennt Umbenennungen, damit Downloads mitwandern;
/// <see cref="Id"/> hält die Kennung einer eingebauten Kategorie fest (sie behält sie auch umbenannt).
/// </summary>
public sealed class CategoryRow : Observable
{
    private string _name = "";
    private string _extensions = "";
    private string _folder = "";

    public string? OriginalKey { get; init; }
    public string? Id { get; init; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Extensions { get => _extensions; set => Set(ref _extensions, value); }
    public string Folder { get => _folder; set => Set(ref _folder, value); }
}

/// <summary>Kategorien anlegen, umbenennen, löschen. Erst „Übernehmen“ speichert.</summary>
public partial class CategoriesSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly ObservableCollection<CategoryRow> _rows = new();

    private CategoriesSheet(MainWindow host, DownloadManager downloads)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        foreach (var c in downloads.Settings.EffectiveCategories)
            _rows.Add(new CategoryRow { OriginalKey = c.Key, Id = c.Id, Name = c.Name, Extensions = c.Extensions, Folder = c.Folder ?? "" });
        Rows.ItemsSource = _rows;
    }

    public static void Open(MainWindow host, DownloadManager downloads) =>
        host.ShowSheet(new CategoriesSheet(host, downloads));

    private void AddRow_Click(object sender, RoutedEventArgs e) => _rows.Add(new CategoryRow { Name = Loc.T("Categories.NewName") });

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CategoryRow row) _rows.Remove(row);
    }

    private void BrowseRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryRow row) return;
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("Categories.FolderFor", row.Name),
            InitialDirectory = Directory.Exists(row.Folder) ? row.Folder : _downloads.Settings.EffectiveDownloadFolder,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) row.Folder = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            string name = row.Name.Trim();
            if (name.Length == 0) { ShowError(Loc.T("Categories.NameRequired")); return; }
            if (!names.Add(name)) { ShowError(Loc.T("Categories.NameTwice", name)); return; }
            string folder = row.Folder.Trim();
            if (folder.Length > 0 && (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder)))
            {
                ShowError(Loc.T("Categories.FolderInvalid", name));
                return;
            }
        }

        var categories = _rows.Select(r => new CategorySetting
        {
            Id = r.Id,
            Name = r.Name.Trim(), // der Standardname einer eingebauten Kategorie bleibt ungespeichert und folgt der Sprache
            Extensions = r.Extensions.Trim(),
            Folder = r.Folder.Trim().Length > 0 ? r.Folder.Trim() : null,
        }).ToList();
        var renames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].OriginalKey is { } old && !string.Equals(old, categories[i].Key, StringComparison.Ordinal))
                renames[old] = categories[i].Key;

        _downloads.UpdateCategories(categories, renames);
        _host.CloseSheet();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
