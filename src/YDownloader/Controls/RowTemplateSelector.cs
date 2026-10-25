using System.Windows;
using System.Windows.Controls;
using YDownloader.Downloads;

namespace YDownloader.Controls;

/// <summary>Wählt für die Hauptliste die Vorlage: ein Download oder ein Paketkopf.</summary>
public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Download { get; set; }
    public DataTemplate? Head { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is PackageHead ? Head : Download;
}
