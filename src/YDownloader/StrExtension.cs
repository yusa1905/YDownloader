using System;
using System.Windows.Markup;

namespace YDownloader;

/// <summary>
/// Text aus der Sprachdatei in XAML: <c>Text="{l:Str Settings.Title}"</c> mit <c>xmlns:l="clr-namespace:YDownloader"</c>.
/// Der Text wird beim Laden der Oberfläche eingesetzt, die Sprache steht dann fest (siehe <see cref="Loc"/>).
/// Der Name weicht bewusst von <c>Loc</c> ab: XAML würde sonst zwischen der Klasse und der Erweiterung wählen müssen.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public StrExtension()
    {
    }

    public StrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
