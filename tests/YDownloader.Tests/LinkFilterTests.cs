using System.Linq;
using Xunit;
using YDownloader.Downloads;

namespace YDownloader.Tests;

public class LinkFilterTests
{
    private const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;

    private static FilterItem Item(string name, long size = -1, string host = "files.example.invalid",
        FilterStatus status = FilterStatus.Unknown) =>
        new(name, $"http://{host}/dl/{name}", size, host, status);

    private static bool Hit(string filter, FilterItem item) => LinkFilter.Parse(filter).Matches(item);

    // ---------------------------------------------------------------- bisherige Schreibweise

    [Fact]
    public void Empty_filter_lets_everything_through()
    {
        Assert.True(LinkFilter.Parse(null).IsEmpty);
        Assert.True(LinkFilter.Parse("   ").IsEmpty);
        Assert.True(Hit("", Item("a.bin")));
    }

    [Theory]
    [InlineData(".mkv 1080p -sample", "Film.1080p.mkv", true)]
    [InlineData(".mkv 1080p -sample", "Film.720p.mkv", false)]
    [InlineData(".mkv 1080p -sample", "Film.1080p.sample.mkv", false)]
    [InlineData(".MKV", "film.mkv", true)]
    [InlineData(".mkv, .avi", "a.avi", true)]
    [InlineData(".mkv, .avi", "a.zip", false)]
    [InlineData(".mkv | .avi", "a.avi", true)]
    [InlineData("*.rar", "a.rar", true)]
    [InlineData("*.rar", "a.rar.txt", false)]
    [InlineData("film?.mkv", "film1.mkv", true)]
    [InlineData("-nfo", "a.nfo", false)]
    [InlineData("-nfo", "a.mkv", true)]
    [InlineData("-", "a.mkv", true)]
    public void Old_syntax_still_works(string filter, string name, bool expected)
        => Assert.Equal(expected, Hit(filter, Item(name)));

    [Fact]
    public void Text_is_also_searched_in_the_link()
    {
        Assert.True(Hit("example", Item("a.bin")));
        Assert.True(LinkFilter.Parse("kunde").Matches("a.bin", "http://x.invalid/kunde/a.bin"));
    }

    // ---------------------------------------------------------------- ext

    [Theory]
    [InlineData("ext:rar", "Film.part1.rar", true)]
    [InlineData("ext:RAR", "film.rar", true)]
    [InlineData("ext:.rar", "film.rar", true)]
    [InlineData("ext:rar", "film.rar.txt", false)]
    [InlineData("ext:rar", "rar", false)]
    [InlineData("ext:rar", "film.", false)]
    [InlineData("ext:r*", "film.rar", true)]
    [InlineData("ext:r*", "film.zip", false)]
    [InlineData("ext:001", "Film.7z.001", true)]
    [InlineData("-ext:nfo", "a.nfo", false)]
    [InlineData("-ext:nfo", "a.mkv", true)]
    [InlineData("ext:rar, ext:zip", "a.zip", true)]
    [InlineData("ext:rar ext:zip", "a.zip", false)]
    public void Ext_matches_the_extension_only(string filter, string name, bool expected)
        => Assert.Equal(expected, Hit(filter, Item(name)));

    // ---------------------------------------------------------------- size

    [Theory]
    [InlineData("size>100MB", 150 * MB, true)]
    [InlineData("size>100MB", 100 * MB, false)]
    [InlineData("size>=100MB", 100 * MB, true)]
    [InlineData("size<2GB", 1 * GB, true)]
    [InlineData("size<2GB", 2 * GB, false)]
    [InlineData("size<=2GB", 2 * GB, true)]
    [InlineData("size=1KB", 1024, true)]
    [InlineData("size:1KB", 1024, true)]
    [InlineData("size>1.5MB", 2 * MB, true)]
    [InlineData("size>1,5MB", 2 * MB, true)]
    [InlineData("size>1.5MB", 1 * MB, false)]
    [InlineData("SIZE>100mb", 150 * MB, true)]
    [InlineData("size>100", 101, true)]
    [InlineData("size>100B", 101, true)]
    [InlineData("size>1k", 2000, true)]
    [InlineData("size>1MiB", 2 * MB, true)]
    [InlineData("size<1MB", 0, true)]
    public void Size_compares_in_binary_units(string filter, long size, bool expected)
        => Assert.Equal(expected, Hit(filter, Item("a.bin", size)));

    [Theory]
    [InlineData("size>1MB")]
    [InlineData("size<1MB")]
    [InlineData("-size<1MB")]
    [InlineData("-size>1MB")]
    public void Unknown_size_never_matches_a_size_term_so_it_is_not_hidden_by_a_negated_one(string filter)
    {
        bool negated = filter.StartsWith('-');
        Assert.Equal(negated, Hit(filter, Item("a.bin", -1)));
    }

    [Fact]
    public void Size_range_combines_with_and()
    {
        string filter = "size>100MB size<2GB";
        Assert.True(Hit(filter, Item("a.bin", 500 * MB)));
        Assert.False(Hit(filter, Item("a.bin", 50 * MB)));
        Assert.False(Hit(filter, Item("a.bin", 3 * GB)));
    }

    [Theory]
    [InlineData("size>viel")]
    [InlineData("size>")]
    [InlineData("size>10XB")]
    [InlineData("size>10MBB")]
    [InlineData("size>99999999999999TB")]
    [InlineData("sizeMB")]
    public void Unreadable_size_is_plain_text_and_matches_nothing_real(string filter)
    {
        Assert.False(Hit(filter, Item("a.bin", 5 * GB)));
        Assert.True(Hit(filter, new FilterItem(filter, "http://x.invalid/" + filter))); // als Text trifft er nur sich selbst
    }

    // ---------------------------------------------------------------- host

    [Theory]
    [InlineData("host:rapidgator", "rapidgator.net", true)]
    [InlineData("host:RapidGator", "www.rapidgator.net", true)]
    [InlineData("host:rapidgator.net", "rapidgator.net", true)]
    [InlineData("host:mega", "rapidgator.net", false)]
    [InlineData("host:*.net", "rapidgator.net", true)]
    [InlineData("host:*.org", "rapidgator.net", false)]
    [InlineData("-host:rapidgator", "rapidgator.net", false)]
    [InlineData("-host:rapidgator", "mega.nz", true)]
    public void Host_matches_the_host_of_the_link(string filter, string host, bool expected)
        => Assert.Equal(expected, Hit(filter, Item("a.bin", host: host)));

    [Fact]
    public void Host_is_read_from_the_link_when_not_given()
    {
        var filter = LinkFilter.Parse("host:example");

        Assert.True(filter.Matches(new FilterItem("a.bin", "http://files.example.invalid/a.bin")));
        Assert.False(filter.Matches(new FilterItem("a.bin", "kein link")));
    }

    [Fact]
    public void Host_term_does_not_match_the_path_of_the_link()
        => Assert.False(Hit("host:film", new FilterItem("a.bin", "http://files.example.invalid/film/a.bin")));

    // ---------------------------------------------------------------- status

    [Theory]
    [InlineData("status:online", FilterStatus.Online, true)]
    [InlineData("status:online", FilterStatus.Offline, false)]
    [InlineData("status:online", FilterStatus.Unknown, false)]
    [InlineData("status:offline", FilterStatus.Offline, true)]
    [InlineData("status:unknown", FilterStatus.Unknown, true)]
    [InlineData("status:unknown", FilterStatus.Online, false)]
    [InlineData("STATUS:Online", FilterStatus.Online, true)]
    [InlineData("-status:offline", FilterStatus.Offline, false)]
    [InlineData("-status:offline", FilterStatus.Online, true)]
    [InlineData("-status:offline", FilterStatus.Unknown, true)]
    public void Status_matches_the_check_result(string filter, FilterStatus status, bool expected)
        => Assert.Equal(expected, Hit(filter, Item("a.bin", status: status)));

    [Fact]
    public void Unknown_status_word_is_plain_text()
        => Assert.False(Hit("status:egal", Item("a.bin")));

    // ---------------------------------------------------------------- Kombinationen

    [Fact]
    public void Terms_combine_with_and_comma_with_or_and_minus_excludes_everywhere()
    {
        var filter = LinkFilter.Parse("ext:rar host:mega, ext:zip size>1MB -status:offline");

        Assert.True(filter.Matches(Item("a.rar", 1, "mega.nz")));
        Assert.False(filter.Matches(Item("a.rar", 1, "other.invalid")));
        Assert.True(filter.Matches(Item("a.zip", 5 * MB)));
        Assert.False(filter.Matches(Item("a.zip", 5 * MB, status: FilterStatus.Offline)));
        Assert.False(filter.Matches(Item("a.rar", 1, "mega.nz", FilterStatus.Offline))); // der Ausschluss gilt für beide Alternativen
        Assert.False(filter.Matches(Item("a.zip", 1)));
    }

    [Fact]
    public void Only_negated_terms_keep_everything_else()
    {
        var filter = LinkFilter.Parse("-ext:nfo -sample -size<1MB");

        Assert.True(filter.Matches(Item("a.mkv", 5 * MB)));
        Assert.True(filter.Matches(Item("a.mkv"))); // Größe unbekannt: nicht von der Größe ausgeschlossen
        Assert.False(filter.Matches(Item("a.nfo", 5 * MB)));
        Assert.False(filter.Matches(Item("a.sample.mkv", 5 * MB)));
        Assert.False(filter.Matches(Item("a.mkv", 10 * KB)));
    }

    [Fact]
    public void Odd_input_does_not_throw()
    {
        foreach (string text in new[] { "ext:", ":", "host:", "status:", "size", "size>", "-ext:", "---", "a,,b", "*", "?", "[", "(", "\\", "ext:[", "host:(" })
        {
            var filter = LinkFilter.Parse(text);
            _ = filter.Matches(Item("a.bin", 5));
        }
    }

    // ---------------------------------------------------------------- Regeln „Automatisch abwählen“

    [Fact]
    public void Rules_deselect_when_any_rule_matches()
    {
        var rules = DeselectRules.Parse(new[] { ".nfo", "sample", "size<1MB" });

        Assert.True(rules.Matches(Item("info.nfo", 5 * MB)));
        Assert.True(rules.Matches(Item("Film.sample.mkv", 5 * MB)));
        Assert.True(rules.Matches(Item("tiny.bin", 10 * KB)));
        Assert.False(rules.Matches(Item("Film.mkv", 5 * MB)));
        Assert.False(rules.Matches(Item("Film.mkv"))); // Größe unbekannt: die Größenregel trifft nicht
        Assert.Equal(3, rules.Count);
    }

    [Fact]
    public void Rules_use_the_same_extended_syntax()
    {
        var rules = DeselectRules.Parse(new[] { "ext:txt host:mega", "status:offline" });

        Assert.True(rules.Matches(Item("a.txt", host: "mega.nz")));
        Assert.False(rules.Matches(Item("a.txt", host: "other.invalid")));
        Assert.True(rules.Matches(Item("a.bin", status: FilterStatus.Offline)));
    }

    [Fact]
    public void Empty_rule_list_and_blank_lines_deselect_nothing()
    {
        Assert.False(DeselectRules.None.Matches(Item("a.nfo")));
        Assert.False(DeselectRules.Parse(null).Matches(Item("a.nfo")));
        var blank = DeselectRules.Parse(new[] { "", "   ", "-" });
        Assert.Equal(0, blank.Count);
        Assert.False(blank.Matches(Item("a.nfo")));
    }

    [Fact]
    public void Rules_are_limited_in_number()
    {
        var many = Enumerable.Range(0, DeselectRules.MaxRules + 20).Select(i => "regel" + i);

        Assert.Equal(DeselectRules.MaxRules, DeselectRules.Parse(many).Count);
    }

    [Fact]
    public void Normalize_trims_dedupes_and_limits()
    {
        var lines = new[] { "  .nfo ", "", ".NFO", "sample", new string('x', 500) };

        var result = DeselectRules.Normalize(lines);

        Assert.Equal(3, result.Count);
        Assert.Equal(".nfo", result[0]);
        Assert.Equal("sample", result[1]);
        Assert.Equal(DeselectRules.MaxRuleLength, result[2].Length);
        Assert.Equal(DeselectRules.MaxRules, DeselectRules.Normalize(Enumerable.Range(0, 200).Select(i => "r" + i)).Count);
        Assert.Empty(DeselectRules.Normalize(null));
    }
}
