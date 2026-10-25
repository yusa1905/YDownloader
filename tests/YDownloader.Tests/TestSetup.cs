using System.Runtime.CompilerServices;

namespace YDownloader.Tests;

/// <summary>
/// Die Tests prüfen Texte auf Deutsch. Ohne festen Wert folgte die Sprache dem Windows des Rechners, auf dem sie laufen.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void FixLanguage() => Loc.Init("de");
}
