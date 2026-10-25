using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace YDownloader.Downloads;

/// <summary>
/// Hängt die Statusgruppen (<see cref="StatusOrder{T}"/>) an die Hauptliste. Sortiert wird nur, wenn bei einem Download
/// die Gruppe wechselt, nie im 4-Hz-Takt. Solange ein Zeilenmenü offen ist oder eine Maustaste in der Liste gedrückt ist,
/// wartet die Umsortierung und läuft danach. Auswahl und Scroll-Position bleiben erhalten.
/// </summary>
public sealed class ListOrdering
{
    private readonly ListBox _list;
    private readonly ListCollectionView _view;
    private readonly ItemCollection _items;
    private readonly StatusOrder<DownloadItem> _order;
    private bool _queued;
    private bool _pending;

    public ListOrdering(ListBox list, ListCollectionView view, ItemCollection items)
    {
        _list = list;
        _view = view;
        _items = items;
        _order = new StatusOrder<DownloadItem>(i => StatusGroups.Of(i.Status), i => _items.IndexOf(i));
        _order.Commit(_items);
        _view.CustomSort = new OrderComparer(_order);

        foreach (var item in _items) item.PropertyChanged += OnItemChanged;
        _items.CollectionChanged += OnItemsChanged;

        list.PreviewMouseUp += (_, _) => Resume();
        list.LostMouseCapture += (_, _) => Resume();
        if (list.ContextMenu is { } menu) menu.Closed += (_, _) => Resume();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _items)
            {
                item.PropertyChanged -= OnItemChanged;
                item.PropertyChanged += OnItemChanged;
            }
            return;
        }
        if (e.OldItems is not null)
            foreach (DownloadItem item in e.OldItems) item.PropertyChanged -= OnItemChanged;
        if (e.NewItems is not null)
            foreach (DownloadItem item in e.NewItems) item.PropertyChanged += OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadItem.Status)) return;
        if (_order.IsStale(_items)) Request();
    }

    private bool Blocked =>
        (_list.ContextMenu?.IsOpen ?? false) || Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed;

    private void Resume()
    {
        if (_pending) Request();
    }

    private void Request()
    {
        if (Blocked) { _pending = true; return; }
        _pending = false;
        if (_queued) return;
        _queued = true;
        _list.Dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    private void Flush()
    {
        _queued = false;
        if (Blocked) { _pending = true; return; }
        if (!_order.IsStale(_items)) return;

        var selected = _list.SelectedItems.Cast<object>().ToList();
        var scroller = FindScrollViewer(_list);
        double offset = scroller?.VerticalOffset ?? 0;

        _order.Commit(_items);
        _view.Refresh();

        if (!_list.SelectedItems.Cast<object>().SequenceEqual(selected))
        {
            _list.SelectedItems.Clear();
            foreach (var item in selected)
                if (_list.Items.Contains(item)) _list.SelectedItems.Add(item); // Zeilen: Downloads und Paketköpfe
        }
        if (scroller is not null)
        {
            _list.UpdateLayout();
            scroller.ScrollToVerticalOffset(offset);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private sealed class OrderComparer : IComparer
    {
        private readonly StatusOrder<DownloadItem> _order;
        public OrderComparer(StatusOrder<DownloadItem> order) => _order = order;
        public int Compare(object? x, object? y) => _order.Compare((DownloadItem)x!, (DownloadItem)y!);
    }
}
