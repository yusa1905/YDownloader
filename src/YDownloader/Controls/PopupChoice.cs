using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace YDownloader.Controls;

/// <summary>Macht aus einem Knopf (Stil <c>PopupButton</c>) eine Auswahl: Klick öffnet ein Menü, der Knopf zeigt den Wert.</summary>
public sealed class PopupChoice
{
    private readonly Button _button;
    private readonly List<(string Label, string? Value)> _options;

    public PopupChoice(Button button, List<(string Label, string? Value)> options, string? value)
    {
        _button = button;
        _options = options;
        Value = value;
        button.Click += Open;
        ShowValue();
    }

    public string? Value { get; private set; }

    public event EventHandler? Changed;

    private void Open(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = _button, Placement = PlacementMode.Bottom, MinWidth = _button.ActualWidth };
        foreach (var (label, value) in _options)
        {
            if (label.Length == 0)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var entry = new MenuItem { Header = label, IsChecked = value == Value };
            entry.Click += (_, _) =>
            {
                Value = value;
                ShowValue();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    private void ShowValue()
    {
        foreach (var (label, value) in _options)
            if (label.Length > 0 && value == Value)
            {
                _button.Content = label;
                return;
            }
        _button.Content = Value;
    }
}
