using System;
using System.IO;
using System.Linq;
using Xunit;

namespace YDownloader.Tests;

/// <summary>
/// Eingebaute Kategorien: feste Kennung, Anzeigename aus den Sprachdateien, Migration alter Dateien. Nur Dateien in einem
/// eigenen Temp-Ordner, nie die echten Daten. Die Sprache wird nicht umgeschaltet (sie ist global, andere Tests laufen parallel);
/// dass der Name der Sprache folgt, ergibt sich daraus, dass nur der Schlüssel der Sprachdatei gespeichert ist.
/// </summary>
public class CategoryTests
{
    private static AppSettings LoadJson(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ydl-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);
            return AppSettings.Load(path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Defaults_have_unique_ids_and_follow_the_language_file()
    {
        var list = CategorySetting.Defaults();
        Assert.Equal(6, list.Count);
        Assert.Equal(6, list.Select(c => c.Id).Distinct().Count());
        Assert.All(list, c => Assert.Null(c.StoredName));
        Assert.Equal(Loc.T("Category.Default.Music"), list.Single(c => c.Id == "music").Name);
        Assert.Equal("builtin:music", list.Single(c => c.Id == "music").Key);
    }

    [Fact]
    public void Display_name_is_never_the_raw_id()
    {
        var settings = new AppSettings();
        _ = settings.EffectiveCategories;
        Assert.Equal(Loc.T("Category.Default.Archives"), settings.CategoryDisplayName("builtin:archives"));
        Assert.Null(settings.CategoryDisplayName(null));
        Assert.Null(settings.CategoryDisplayName("builtin:gibtesnicht"));

        // Umbenannt behält den eigenen Namen, auch wenn die Kategorie aus der Liste gelöscht ist, kommt der Standardname.
        settings.EffectiveCategories.Single(c => c.Id == "archives").Name = "Pakete";
        Assert.Equal("Pakete", settings.CategoryDisplayName("builtin:archives"));
        settings.Categories = settings.EffectiveCategories.Where(c => c.Id != "videos").ToList();
        Assert.Equal(Loc.T("Category.Default.Videos"), settings.CategoryDisplayName("builtin:videos"));

        // Eigene Kategorie: ihr Name; Standardnamen gibt es in jeder Sprache ohne Kennung.
        Assert.Equal("Eigene", settings.CategoryDisplayName("Eigene"));
        foreach (var (_, nameKey, _) in CategorySetting.BuiltIns)
            Assert.All(Loc.TextsInAllLanguages(nameKey), t => Assert.DoesNotContain("builtin:", t));
    }

    [Fact]
    public void Unrenamed_defaults_store_no_name()
    {
        var settings = new AppSettings();
        _ = settings.EffectiveCategories;
        string json = System.Text.Encoding.UTF8.GetString(settings.ToJson());
        Assert.Contains("\"id\": \"videos\"", json);
        Assert.DoesNotContain("\"name\"", json);
        Assert.Contains("\"categoriesVersion\": 1", json);
    }

    [Fact]
    public void Every_default_name_is_known_in_the_language_files()
    {
        foreach (var (id, nameKey, _) in CategorySetting.BuiltIns)
        {
            var names = Loc.TextsInAllLanguages(nameKey);
            Assert.InRange(names.Count, 1, Loc.Languages.Length); // gleiche Schreibweisen (Videos in de und en) zählen einmal
            Assert.All(names, n => Assert.Equal(id, CategorySetting.BuiltInIdForName(n)));
        }
    }

    [Fact]
    public void Legacy_file_gets_ids_for_names_of_any_language_and_keeps_everything_else()
    {
        var settings = LoadJson("""
            {
              "categories": [
                { "name": "Videos", "extensions": "mkv mp4", "folder": "D:\\Filme" },
                { "name": "Musik", "extensions": "mp3" },
                { "name": "Imágenes", "extensions": "jpg" },
                { "name": "Müzik", "extensions": "flac" },
                { "name": "Spiele", "extensions": "iso" },
                { "name": "Filme", "extensions": "avi" }
              ]
            }
            """);
        var c = settings.Categories!;
        Assert.True(settings.CategoriesMigrated);
        Assert.Equal(1, settings.CategoriesVersion);
        Assert.Equal("videos", c[0].Id);
        Assert.Null(c[0].StoredName);
        Assert.Equal("mkv mp4", c[0].Extensions);
        Assert.Equal("D:\\Filme", c[0].Folder);
        Assert.Equal("music", c[1].Id);
        Assert.Equal("pictures", c[2].Id);
        Assert.Null(c[3].Id);          // "music" ist schon vergeben, die zweite bleibt eine eigene Kategorie
        Assert.Equal("Müzik", c[3].Name);
        Assert.Null(c[4].Id);
        Assert.Equal("Spiele", c[4].Name);
        Assert.Null(c[5].Id);
        Assert.Equal("Filme", c[5].Name);
    }

    [Fact]
    public void Migration_writes_nothing_for_defaults_and_runs_once()
    {
        var fresh = LoadJson("{ \"maxConcurrent\": 2 }");
        Assert.False(fresh.CategoriesMigrated);
        Assert.Equal(1, fresh.CategoriesVersion);

        var again = LoadJson("{ \"categoriesVersion\": 1, \"categories\": [ { \"name\": \"Videos\" } ] }");
        Assert.False(again.CategoriesMigrated);
        Assert.Null(again.Categories![0].Id); // bewusst als eigene Kategorie gespeichert, nicht erneut erraten
    }

    [Fact]
    public void Renamed_builtin_keeps_its_id_and_name_and_resetting_clears_it()
    {
        var c = CategorySetting.Defaults().Single(x => x.Id == "videos");
        c.Name = "Filme";
        Assert.Equal("Filme", c.StoredName);
        Assert.Equal("Filme", c.Name);
        Assert.Equal("builtin:videos", c.Key);

        c.Name = Loc.T("Category.Default.Videos").ToUpperInvariant();
        Assert.Null(c.StoredName);
    }

    [Fact]
    public void Renamed_builtin_survives_a_roundtrip()
    {
        var settings = new AppSettings { Categories = CategorySetting.Defaults(), CategoriesVersion = 1 };
        settings.Categories[1].Name = "Songs";
        string dir = Path.Combine(Path.GetTempPath(), "ydl-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllBytes(path, settings.ToJson());
            var back = AppSettings.Load(path);
            Assert.False(back.CategoriesMigrated);
            Assert.Equal("music", back.Categories![1].Id);
            Assert.Equal("Songs", back.Categories[1].Name);
            Assert.Null(back.Categories[0].StoredName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Automatic_assignment_does_not_depend_on_names()
    {
        var settings = new AppSettings();
        foreach (var c in settings.EffectiveCategories) c.Name = "x" + c.Id;
        Assert.Equal("videos", settings.CategoryFor("a.MKV")!.Id);
        Assert.Equal("archives", settings.CategoryFor("b.zip")!.Id);
        Assert.Null(settings.CategoryFor("c.unknown"));
    }

    [Fact]
    public void References_from_old_downloads_are_resolved_to_keys()
    {
        var settings = LoadJson("""
            { "categories": [ { "name": "Videos", "extensions": "mkv" }, { "name": "Filme", "extensions": "avi" },
                              { "name": "Spiele", "extensions": "iso" } ] }
            """);
        Assert.Equal("builtin:videos", settings.ResolveCategoryRef("Videos"));
        Assert.Equal("builtin:videos", settings.ResolveCategoryRef("videos"));       // Name, ohne Rücksicht auf Groß/Klein
        Assert.Equal("builtin:videos", settings.ResolveCategoryRef("Vidéos"));       // Name aus einer anderen Sprache
        Assert.Equal("builtin:videos", settings.ResolveCategoryRef("builtin:videos"));
        Assert.Equal("Filme", settings.ResolveCategoryRef("Filme"));
        Assert.Equal("Spiele", settings.ResolveCategoryRef("Spiele"));
        Assert.Equal("Gelöscht", settings.ResolveCategoryRef("Gelöscht"));           // unbekannt bleibt, wie es ist
        Assert.Null(settings.ResolveCategoryRef(null));
    }

    [Fact]
    public void Renamed_builtin_is_found_by_key_and_by_its_new_name()
    {
        var settings = new AppSettings();
        settings.EffectiveCategories.Single(c => c.Id == "music").Name = "Songs";
        Assert.Equal("music", settings.FindCategory("builtin:music")!.Id);
        Assert.Equal("music", settings.FindCategory("songs")!.Id);
        Assert.Equal("builtin:music", settings.ResolveCategoryRef("Songs"));
        Assert.Equal("builtin:music", settings.ResolveCategoryRef("Musik")); // alter Name aus der Zeit vor dem Umbenennen
    }
}
