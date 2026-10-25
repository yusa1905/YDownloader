using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using YDownloader.Downloads;
using YDownloader.Packages;
using YDownloader.Sheets;

namespace YDownloader;

public partial class MainWindow : Window
{
    private const string KeyClose = "LightClose", KeyMin = "LightMinimize", KeyZoom = "LightZoom", KeyInactive = "LightInactive";

    private readonly DownloadManager _downloads;
    private readonly Sidebar _sidebar;
    private readonly ICollectionView _view;
    private readonly ListOrdering? _ordering;
    private readonly PackageGrouping _grouping = new();
    private readonly RangeCollection<object> _rows = new();
    private SidebarEntry _current;
    private int _lastCount;
    private double _scale = 1;

    public MainWindow(DownloadManager downloads)
    {
        InitializeComponent();
        _sidebar = new Sidebar(); // nach dem XAML: Die Texte brauchen die Sprache, die erst dann sicher feststeht
        _downloads = downloads;
        DataContext = downloads;

        SidebarList.ItemsSource = _sidebar.Entries;
        _current = _sidebar.Rebuild(downloads.Settings.EffectiveCategories, downloads.Settings.Queues, null);
        SidebarList.SelectedItem = _current;
        downloads.ListChanged += (_, _) => OnListChanged();
        downloads.AfterCompletion.CountdownStarted += (_, _) => CountdownSheet.Open(this, downloads.AfterCompletion);
        downloads.AfterCompletion.PropertyChanged += OnAfterCompletionChanged;

        // Die Seitenleiste filtert die Liste. Live: Wechselt Zustand oder Kategorie, wandert der Eintrag sofort.
        _view = CollectionViewSource.GetDefaultView(downloads.Items);
        _view.Filter = o => _current.Matches((DownloadItem)o);
        if (_view is ICollectionViewLiveShaping { CanChangeLiveFiltering: true } live)
        {
            live.LiveFilteringProperties.Add(nameof(DownloadItem.Status));
            live.LiveFilteringProperties.Add(nameof(DownloadItem.Category));
            live.LiveFilteringProperties.Add(nameof(DownloadItem.QueueId));
            live.IsLiveFiltering = true;
        }

        // Gruppen: Laufend und Wartend, dann Angehalten und Fehler, dann Fertig. Sortiert wird nur bei einem Gruppenwechsel.
        if (_view is ListCollectionView listView) _ordering = new ListOrdering(List, listView, downloads.Items);

        // Die Liste zeigt Zeilen: Downloads und Paketköpfe. Sie entstehen aus der gefilterten, sortierten Ansicht und werden
        // bei jeder Änderung der Ansicht abgeglichen (Auswahl und Scroll-Position bleiben). Zähler und Statusleiste rechnen
        // weiter über downloads.Items, nie über die Zeilen.
        List.ItemsSource = _rows;
        _view.CollectionChanged += (_, _) => RebuildRows();
        downloads.Ticked += (_, _) => _grouping.UpdateRunning();
        RebuildRows();

        // Symbole in einer 6x6-Box (Größe aus den Theme-Ressourcen).
        Light.SetGlyph(BtnClose, Geometry.Parse("M0,0 L6,6 M6,0 L0,6"));
        Light.SetGlyph(BtnMin, Geometry.Parse("M0,3 L6,3"));
        Light.SetGlyphFill(BtnZoom, Geometry.Parse("M6,0 L2.5,0 L6,3.5 Z M0,6 L3.5,6 L0,2.5 Z"));
        BtnClose.SetResourceReference(Light.GlyphBrushProperty, "LightGlyphClose");
        BtnMin.SetResourceReference(Light.GlyphBrushProperty, "LightGlyphMinimize");
        BtnZoom.SetResourceReference(Light.GlyphBrushProperty, "LightGlyphZoom");
        UpdateLights();
    }

    public DownloadManager Downloads => _downloads;

    /// <summary>Nachdem die Einstellungen gelesen sind, noch vor dem ersten Anzeigen.</summary>
    public void ApplySettings()
    {
        RefreshCategories();
        // Läuft ab hier im Hintergrund; die Abfrage blockiert nie den UI-Thread. Erst nach dem ersten Anzeigen, damit der
        // Start nichts davon merkt.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, _downloads.StartFreeSpace);
    }

    // ---------------------------------------------------------------- Seitenleiste

    private void RefreshCategories()
    {
        // Kategorien und Queues neu aufbauen, nur wenn sie sich geändert haben (sonst springt die Auswahl).
        var settings = _downloads.Settings;
        if (!_sidebar.IsCurrent(settings.EffectiveCategories, settings.Queues))
        {
            SidebarList.SelectedItem = _sidebar.Rebuild(settings.EffectiveCategories, settings.Queues, _current);
        }
    }

    /// <summary>Baut die Zeilen der Liste aus der Ansicht neu und gleicht sie mit den vorhandenen ab.</summary>
    internal void RebuildRows()
    {
        var ordered = _view.Cast<DownloadItem>().ToList();
        PackageGrouping.Sync(_rows, _grouping.Build(ordered));
    }

    private void OnListChanged()
    {
        _grouping.UpdateAll(); // Zustandswechsel und Entpacken ändern die Köpfe auch ohne Takt
        RefreshCategories();
        // Neu hinzugefügt, aber in der gewählten Ansicht unsichtbar: zu „Alle“ wechseln, sonst wirkt es verschluckt.
        var items = _downloads.Items;
        if (items.Count > _lastCount && !_current.Matches(items[^1])) SidebarList.SelectedItem = _sidebar.All;
        _lastCount = items.Count;

        _sidebar.Recount(items);
        UpdateEmptyState();
    }

    private void Sidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is not SidebarEntry entry || entry.IsHeader) return;
        if (entry == _current) return;
        _current = entry;
        _view.Refresh();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool none = _downloads.Items.Count == 0;
        EmptyState.Visibility = none || _current.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = none ? Loc.T("Main.Empty.Title") : Loc.T("Main.Empty.NothingIn", _current.Title);
        EmptyHint.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SidebarEdit_Click(object sender, RoutedEventArgs e)
    {
        if (IsSheetOpen || ((FrameworkElement)sender).DataContext is not SidebarEntry entry) return;
        if (entry.Edit == SidebarEdit.Categories) CategoriesSheet.Open(this, _downloads);
        else if (entry.Edit == SidebarEdit.Queues) QueuesSheet.Open(this, _downloads);
    }

    /// <summary>Scheitert die Aktion (etwa Ruhezustand nicht eingeschaltet), steht das im Fenster, nicht nur im Tray.</summary>
    private async void OnAfterCompletionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Power.AfterCompletion.Error) || _downloads.AfterCompletion.Error is not { } error) return;
        Reveal();
        await ConfirmSheet.AskAsync(this, Loc.T("Main.ActionFailed"), error, Loc.T("Common.OK"));
    }

    private void After_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen) AfterSheet.Open(this, _downloads);
    }

    private void MenuExtract_Click(object sender, RoutedEventArgs e) => _downloads.ExtractNow(Selected());

    private void MenuPassword_Click(object sender, RoutedEventArgs e) => PasswordSheet.Open(this, _downloads, Selected());

    private void MenuAfter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen && Selected() is [var item]) AfterSheet.Open(this, _downloads, item);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSheetOpen) SettingsSheet.Open(this, _downloads);
    }

    // ---------------------------------------------------------------- Fenster und Ampel

    /// <summary>
    /// Die Skalierung wirkt per LayoutTransform auf den Inhalt. Das Fenster wächst mit, damit der Inhalt gleich viel Platz
    /// behält. Titelleiste (Ziehen) und Mindestgröße rechnen in Fensterpunkten und müssen mitgehen.
    /// </summary>
    public void OnScaleChanged(double scale)
    {
        double ratio = scale / _scale;
        _scale = scale;
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = (double)FindResource("TitleBarHeight") * scale;

        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min((double)FindResource("WindowMinWidth") * scale, work.Width);
        MinHeight = Math.Min((double)FindResource("WindowMinHeight") * scale, work.Height);
        if (WindowState != WindowState.Normal) return;

        Width = Math.Min(Width * ratio, work.Width);
        Height = Math.Min(Height * ratio, work.Height);
        if (IsVisible)
        {
            Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
        }
    }

    /// <summary>Gesetzt, wenn wirklich beendet wird (Tray-Menü, Strg+Q, Abmelden).</summary>
    internal bool ForceClose;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ForceClose || e.Cancel) return;
        if (_downloads.Settings.CloseAction != CloseAction.Tray || !((App)Application.Current).CanHideToTray) return;
        e.Cancel = true;
        Hide();
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); UpdateLights(); }
    protected override void OnDeactivated(EventArgs e) { base.OnDeactivated(e); UpdateLights(); }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Maximiert ragt ein WindowChrome-Fenster über den Bildschirmrand hinaus.
        Root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.FixedFrameVerticalBorderWidth,
                SystemParameters.WindowResizeBorderThickness.Top + SystemParameters.FixedFrameHorizontalBorderHeight,
                SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.FixedFrameVerticalBorderWidth,
                SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight)
            : new Thickness(0);
    }

    // Farbe nur bei aktivem Fenster oder beim Überfahren der Ampel, sonst grau. Symbole nur beim Überfahren.
    private void UpdateLights()
    {
        bool hover = Lights.IsMouseOver;
        bool colored = IsActive || hover;
        Apply(BtnClose, colored ? KeyClose : KeyInactive, hover);
        Apply(BtnMin, colored ? KeyMin : KeyInactive, hover);
        Apply(BtnZoom, colored ? KeyZoom : KeyInactive, hover);
    }

    private static void Apply(Button b, string key, bool glyph)
    {
        b.SetResourceReference(Light.FillProperty, key);
        Light.SetGlyphVisibility(b, glyph ? Visibility.Visible : Visibility.Collapsed);
    }

    private void Lights_MouseChanged(object sender, MouseEventArgs e) => UpdateLights();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Zoom_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------------------------------------------------------------- Sheets

    public bool IsSheetOpen => SheetLayer.Visibility == Visibility.Visible;

    /// <summary>Zeigt ein Sheet modal unter der Titelleiste. Liste und Werkzeuge sind solange gesperrt.</summary>
    public void ShowSheet(FrameworkElement sheet, IInputElement? focus = null)
    {
        SheetHost.Content = sheet;
        SheetLayer.Visibility = Visibility.Visible;
        Main.IsEnabled = false;
        Toolbar.IsEnabled = false;
        Dispatcher.InvokeAsync(() => (focus ?? sheet).Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Schließt das Sheet nur, wenn gerade dieses zu sehen ist (nicht ein anderes, das inzwischen darüber liegt).</summary>
    public void CloseSheetIfShowing(FrameworkElement sheet)
    {
        if (SheetHost.Content == sheet) CloseSheet();
    }

    /// <summary>Holt das Fenster aus dem Tray oder der Taskleiste nach vorn.</summary>
    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    public void CloseSheet()
    {
        SheetLayer.Visibility = Visibility.Collapsed;
        SheetHost.Content = null;
        Main.IsEnabled = true;
        Toolbar.IsEnabled = true;
        List.Focus();
        if (_pendingLinks is { } pending)
        {
            var passwords = _pendingPasswords;
            _pendingLinks = null;
            _pendingPasswords = new();
            OpenAdd(pending, passwords);
        }
    }

    private string? _pendingLinks;
    private List<string> _pendingPasswords = new();

    /// <summary>
    /// Links aus der Browser-Erweiterung: Fenster nach vorn, Hinzufügen-Dialog mit den Links. Ist gerade ein anderes Sheet offen
    /// (Einstellungen, Rückfrage), bleiben die Links vorgemerkt, bis es zu ist, und gehen nicht verloren.
    /// </summary>
    public void ReceiveLinks(string text, IReadOnlyList<string>? passwords = null)
    {
        if (!LinkParser.ContainsLink(text)) return;
        Reveal();
        if (IsSheetOpen && SheetHost.Content is not AddSheet)
        {
            _pendingLinks = _pendingLinks is null ? text : _pendingLinks + Environment.NewLine + text;
            if (passwords is not null) _pendingPasswords = PasswordRules.Normalize(_pendingPasswords.Concat(passwords));
        }
        else
            OpenAdd(text, passwords);
    }

    // ---------------------------------------------------------------- Werkzeugleiste und Tasten

    private void Add_Click(object sender, RoutedEventArgs e) => OpenAdd(ClipboardLinks());
    private void StartAll_Click(object sender, RoutedEventArgs e) => _downloads.StartAll();
    private void PauseAll_Click(object sender, RoutedEventArgs e) => _downloads.PauseAll();
    private void RemoveCompleted_Click(object sender, RoutedEventArgs e) => _downloads.RemoveCompleted();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || IsSheetOpen || Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key is Key.V or Key.N)
        {
            OpenAdd(ClipboardLinks());
            e.Handled = true;
        }
        else if (e.Key == Key.OemComma)
        {
            SettingsSheet.Open(this, _downloads);
            e.Handled = true;
        }
        else if (e.Key == Key.Q)
        {
            ((App)Application.Current).Quit();
            e.Handled = true;
        }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        switch (e.Key)
        {
            case Key.Delete:
                RemoveAsk(selected);
                break;
            case Key.Space:
                ToggleAll(selected);
                break;
            case Key.Enter:
                // Köpfe öffnen keine Dateien (ein Paket hat viele Teile): Enter klappt sie auf und zu.
                foreach (var head in List.SelectedItems.OfType<PackageHead>().ToList()) ToggleHead(head);
                foreach (var item in SelectedRows().Where(i => i.Status == DownloadStatus.Completed)) OpenFile(item);
                break;
            case Key.Right or Key.Left when List.SelectedItems.OfType<PackageHead>().Any() && Keyboard.Modifiers == ModifierKeys.None:
                foreach (var head in List.SelectedItems.OfType<PackageHead>().ToList())
                    if (head.IsExpanded == (e.Key == Key.Left)) ToggleHead(head);
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                CopyLinks(selected);
                break;
            case Key.F2:
                EditSheet.Open(this, _downloads, selected);
                break;
            case Key.I when Keyboard.Modifiers == ModifierKeys.Control:
                if (selected.Count != 1) return; // wie das Menü: Details nur für genau einen Download, nicht für ein Paket
                DetailSheet.Open(this, selected[0]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        switch (RowAt(e.OriginalSource))
        {
            case DownloadItem item:
                DetailSheet.Open(this, item);
                break;
            case PackageHead head:
                ToggleHead(head); // der Kopf hat kein Detail-Sheet: Doppelklick klappt auf und zu
                break;
        }
    }

    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (item.Status == DownloadStatus.Completed) ShowInFolder(item);
        else _downloads.Toggle(item);
    }

    /// <summary>Zeilenknopf des Kopfs: alle Teile starten oder anhalten, bei fertigem Paket den Ordner zeigen.</summary>
    private void HeadAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PackageHead head || head.Members.Count == 0) return;
        if (head.Status == DownloadStatus.Completed) ShowInFolder(head.Members[0]);
        else ToggleAll(head.Members.ToList());
    }

    private void HeadToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PackageHead head) ToggleHead(head);
    }

    private void ToggleHead(PackageHead head)
    {
        _grouping.Toggle(head);
        RebuildRows();
    }

    /// <summary>Öffnet den Hinzufügen-Dialog. Ist er schon offen, kommen die Links dazu.</summary>
    public void OpenAdd(string? text, IReadOnlyList<string>? passwords = null)
    {
        if (SheetHost.Content is AddSheet open)
        {
            if (!string.IsNullOrEmpty(text)) open.Append(text);
            open.AddPasswords(passwords);
            return;
        }
        if (IsSheetOpen) return; // eine andere Rückfrage ist offen
        var sheet = AddSheet.Open(this, _downloads, text, _current.Kind == SidebarKind.Category ? _current.Category : null,
            _current.Kind == SidebarKind.Queue ? _current.QueueId : null);
        sheet.AddPasswords(passwords);
    }

    public static string? ClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (COMException) { return null; } // Zwischenablage gerade von einem anderen Programm belegt
    }

    /// <summary>Text der Zwischenablage, aber nur, wenn ein Link darin steckt.</summary>
    private static string? ClipboardLinks()
    {
        string? text = ClipboardText();
        return LinkParser.ContainsLink(text) ? text : null;
    }

    // ---------------------------------------------------------------- Drag & Drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = !IsSheetOpen || SheetHost.Content is AddSheet;
        ok &= DropData.CanRead(e.Data);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok && !IsSheetOpen ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        string? text = DropData.Read(e.Data);
        if (LinkParser.ContainsLink(text)) OpenAdd(text);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- Kontextmenü

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        foreach (var obj in ((ContextMenu)sender).Items)
        {
            if (obj is not MenuItem menu) continue;
            menu.IsEnabled = selected.Count > 0;
            switch (menu.Tag as string)
            {
                case "start":
                    // Auch ein Download, der auf den Zeitplan seiner Queue wartet, lässt sich sofort starten.
                    menu.Visibility = Visible(selected.Any(i =>
                        i.Status is DownloadStatus.Paused or DownloadStatus.Failed || (i.Status == DownloadStatus.Queued && i.WaitNote is not null)));
                    break;
                case "pause":
                    menu.Visibility = Visible(selected.Any(i => i.Status is DownloadStatus.Running or DownloadStatus.Queued));
                    break;
                case "open":
                    // Nur direkt markierte Downloads: Ein Paketkopf öffnet nicht alle Teile auf einmal.
                    menu.IsEnabled = SelectedRows().Any(i => i.Status == DownloadStatus.Completed);
                    break;
                case "details":
                    menu.IsEnabled = selected.Count == 1;
                    break;
                case "extract":
                    menu.Visibility = Visible(selected.Any(_downloads.CanOfferExtract)); // nur bei Paketen und Archiven
                    menu.IsEnabled = selected.Any(_downloads.CanExtract);
                    break;
                case "password":
                    menu.Visibility = Visible(selected.Any(_downloads.CanOfferExtract));
                    menu.IsEnabled = selected.Any(_downloads.CanEnterPassword);
                    break;
                case "category":
                    FillCategoryMenu(menu, selected);
                    break;
                case "after":
                    menu.IsEnabled = selected.Count == 1 && selected[0].Status != DownloadStatus.Completed;
                    break;
                case "queue":
                    menu.Visibility = Visible(_downloads.Settings.Queues.Count > 0); // ohne eigene Queues gibt es nichts zu wählen
                    FillQueueMenu(menu, selected);
                    break;
            }
        }

        // Ohne „Starten“ und „Anhalten“ (z. B. nur Fertige markiert) bliebe die erste Trennlinie oben stehen.
        var items = ((ContextMenu)sender).Items.Cast<object>().ToList();
        bool anyAction = items.OfType<MenuItem>().Any(m => m.Tag as string is "start" or "pause" && m.Visibility == Visibility.Visible);
        foreach (var sep in items.OfType<Separator>().Where(s => s.Tag as string == "startSeparator"))
            sep.Visibility = Visible(anyAction);
    }

    /// <summary>Untermenü „Kategorie“: Haken bei der Kategorie, die alle markierten Downloads teilen.</summary>
    private void FillCategoryMenu(MenuItem menu, List<DownloadItem> selected)
    {
        menu.Items.Clear();
        string? shared = selected.Count > 0 && selected.All(i => i.Category == selected[0].Category) ? selected[0].Category : "\u0001";
        void Add(string header, string? category)
        {
            var entry = new MenuItem { Header = header, IsChecked = shared == category };
            entry.Click += (_, _) => _downloads.SetCategory(selected, category);
            menu.Items.Add(entry);
        }
        Add(Loc.T("Category.None"), null);
        if (_downloads.Settings.EffectiveCategories.Count > 0) menu.Items.Add(new Separator());
        foreach (var c in _downloads.Settings.EffectiveCategories) Add(c.Name, c.Key);
    }

    /// <summary>Untermenü „Queue“: Haken bei der Queue, die alle markierten Downloads teilen.</summary>
    private void FillQueueMenu(MenuItem menu, List<DownloadItem> selected)
    {
        menu.Items.Clear();
        bool sameQueue = selected.Count > 0 && selected.All(i => i.QueueId == selected[0].QueueId);
        string? shared = sameQueue ? selected[0].QueueId : "\u0001";
        void Add(string header, string? queueId)
        {
            var entry = new MenuItem { Header = header, IsChecked = shared == queueId };
            entry.Click += (_, _) => _downloads.SetQueue(selected, queueId);
            menu.Items.Add(entry);
        }
        Add(Loc.T("Queue.Default"), null);
        foreach (var q in _downloads.Settings.Queues) Add(q.Name, q.Id);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void MenuStart_Click(object sender, RoutedEventArgs e) => _downloads.Start(Selected());
    private void MenuPause_Click(object sender, RoutedEventArgs e) => _downloads.Pause(Selected());
    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SelectedRows().Where(i => i.Status == DownloadStatus.Completed)) OpenFile(item);
    }
    private void MenuShow_Click(object sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is { } item) ShowInFolder(item);
    }
    private void MenuCopy_Click(object sender, RoutedEventArgs e) => CopyLinks(Selected());
    private void MenuExport_Click(object sender, RoutedEventArgs e) => ExportLinks(Selected());
    private void MenuDetails_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is [var item]) DetailSheet.Open(this, item);
    }
    private void MenuEdit_Click(object sender, RoutedEventArgs e) => EditSheet.Open(this, _downloads, Selected());
    private void MenuRemove_Click(object sender, RoutedEventArgs e) => RemoveAsk(Selected());

    // ---------------------------------------------------------------- Aktionen

    /// <summary>Die markierten Downloads. Ein markierter Paketkopf steht für alle seine sichtbaren Teile, jeder Download einmal.</summary>
    private List<DownloadItem> Selected() => PackageGrouping.Expand(List.SelectedItems.Cast<object>());

    /// <summary>Nur die direkt markierten Download-Zeilen, ohne die Teile markierter Köpfe (für „Datei öffnen“).</summary>
    private List<DownloadItem> SelectedRows() => List.SelectedItems.OfType<DownloadItem>().ToList();

    private static object? RowAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem row) return row.DataContext;
            if (d is Button) return null; // Klick auf einen Zeilenknopf
        }
        return null;
    }

    private void ToggleAll(List<DownloadItem> items)
    {
        // Wie ein Schalter: läuft irgendeiner, wird alles angehalten, sonst alles gestartet.
        if (items.Any(i => i.Status is DownloadStatus.Running or DownloadStatus.Queued)) _downloads.Pause(items);
        else _downloads.Start(items);
    }

    /// <summary>Fragt nach, wenn dabei schon geladene Teildaten verloren gehen.</summary>
    private async void RemoveAsk(List<DownloadItem> items)
    {
        int partial = items.Count(i => i.Status != DownloadStatus.Completed && i.DownloadedBytes > 0);
        if (partial > 0)
        {
            string question = Loc.Plural("Main.Remove.Ask", items.Count);
            string message = Loc.Plural("Main.Remove.Partial", partial);
            if (!await ConfirmSheet.AskAsync(this, question, message, Loc.T("Menu.Remove"))) return;
        }
        _downloads.Remove(items);
    }

    private static void CopyLinks(List<DownloadItem> items)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.Url))); }
        catch (COMException) { } // Zwischenablage gerade von einem anderen Programm belegt
    }

    /// <summary>Speichert die Links der markierten Downloads (die Hoster-Links, nie entsperrte) als Textdatei.</summary>
    private async void ExportLinks(List<DownloadItem> items)
    {
        if (items.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Export.Title"),
            Filter = Loc.T("Export.Filter"),
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = LinkExport.SuggestedName(items),
            InitialDirectory = _downloads.Settings.EffectiveDownloadFolder,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            LinkExport.Write(dialog.FileName, items.Select(i => i.Url));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ConfirmSheet.AskAsync(this, Loc.T("Export.Failed"), ex.Message, Loc.T("Common.Close"));
        }
    }

    private static void OpenFile(DownloadItem item)
    {
        if (File.Exists(item.ShownPath)) Shell(item.ShownPath);
    }

    /// <summary>Markiert die Datei im Explorer. Unfertig: die .part, sonst nur der Ordner.</summary>
    private static void ShowInFolder(DownloadItem item)
    {
        string path = item.ShownPath;
        if (!File.Exists(path)) path = item.TargetPath + ".part";
        if (File.Exists(path)) Run("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(item.Folder)) Shell(item.Folder);
    }

    private static void Shell(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private static void Run(string exe, string args) => Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });

    private static void Start(ProcessStartInfo info)
    {
        try { Process.Start(info)?.Dispose(); }
        catch (Win32Exception) { } // keine Anwendung für den Dateityp oder Abbruch durch den Benutzer
    }
}
