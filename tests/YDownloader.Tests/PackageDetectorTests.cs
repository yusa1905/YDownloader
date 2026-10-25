using System.Linq;
using Xunit;
using YDownloader.Packages;

namespace YDownloader.Tests;

public class PackageDetectorTests
{
    [Theory]
    [InlineData("Film.part1.rar", "Film", PartKind.RarPart, 1)]
    [InlineData("Film.part01.rar", "Film", PartKind.RarPart, 1)]
    [InlineData("Film.PART12.RAR", "Film", PartKind.RarPart, 12)]
    [InlineData("My.Show.S01.part003.rar", "My.Show.S01", PartKind.RarPart, 3)]
    [InlineData("Film.7z.001", "Film", PartKind.SevenZip, 1)]
    [InlineData("Film.zip.002", "Film", PartKind.ZipSplit, 2)]
    [InlineData("Film.r00", "Film", PartKind.RarOld, 1)]
    [InlineData("Film.r15", "Film", PartKind.RarOld, 16)]
    [InlineData("Film.rar", "Film", PartKind.RarOld, 0)]
    [InlineData("Film.z01", "Film", PartKind.ZipOld, 1)]
    [InlineData("Film.zip", "Film", PartKind.ZipOld, int.MaxValue)]
    public void Parse_reads_parts(string name, string baseName, PartKind kind, int order)
    {
        var p = PackageDetector.Parse(name);
        Assert.NotNull(p);
        Assert.Equal(baseName, p!.Base);
        Assert.Equal(kind, p.Kind);
        Assert.Equal(order, p.Order);
    }

    [Theory]
    [InlineData("Film.mkv")]
    [InlineData("Film.7z")]
    [InlineData("Film.tar.gz")]
    [InlineData("Film.r1")]
    [InlineData("Film.7z.01")]
    [InlineData("readme.txt")]
    [InlineData("")]
    [InlineData(".rar")]
    public void Parse_ignores_other_files(string name) => Assert.Null(PackageDetector.Parse(name));

    [Fact]
    public void Single_archives_are_no_packages()
    {
        var groups = PackageDetector.Group(new[] { "a.rar", "b.zip", "c.part1.rar", "d.mkv" }, s => s);
        Assert.Equal(4, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsPackage));
    }

    [Fact]
    public void Part_rar_forms_one_package_in_part_order()
    {
        var groups = PackageDetector.Group(new[] { "Film.part3.rar", "Film.part1.rar", "Film.part2.rar" }, s => s);
        var g = Assert.Single(groups);
        Assert.True(g.IsPackage);
        Assert.Equal("Film", g.Name);
        Assert.Equal(PartKind.RarPart, g.Kind);
        Assert.Equal(new[] { "Film.part1.rar", "Film.part2.rar", "Film.part3.rar" }, g.Items);
        Assert.Empty(g.Missing);
    }

    [Fact]
    public void Old_rar_starts_with_the_rar_file()
    {
        var g = Assert.Single(PackageDetector.Group(new[] { "x.r01", "x.r00", "x.rar" }, s => s));
        Assert.Equal(new[] { "x.rar", "x.r00", "x.r01" }, g.Items);
        Assert.Empty(g.Missing);
    }

    [Fact]
    public void Old_zip_ends_with_the_zip_file()
    {
        var g = Assert.Single(PackageDetector.Group(new[] { "x.zip", "x.z02", "x.z01" }, s => s));
        Assert.Equal(new[] { "x.z01", "x.z02", "x.zip" }, g.Items);
        Assert.Empty(g.Missing);
    }

    [Fact]
    public void Split_7z_is_one_package()
    {
        var g = Assert.Single(PackageDetector.Group(new[] { "a.7z.002", "a.7z.001" }, s => s));
        Assert.Equal(PartKind.SevenZip, g.Kind);
        Assert.Equal(new[] { "a.7z.001", "a.7z.002" }, g.Items);
    }

    [Fact]
    public void Names_match_ignoring_case_and_kinds_do_not_mix()
    {
        var groups = PackageDetector.Group(new[] { "Film.PART1.rar", "film.part2.rar", "Film.7z.001", "Film.7z.002" }, s => s);
        Assert.Equal(2, groups.Count);
        Assert.Equal(PartKind.RarPart, groups[0].Kind);
        Assert.Equal(PartKind.SevenZip, groups[1].Kind);
    }

    [Fact]
    public void Different_names_make_different_packages()
    {
        var groups = PackageDetector.Group(new[] { "A.part1.rar", "B.part1.rar", "A.part2.rar", "B.part2.rar" }, s => s);
        Assert.Equal(2, groups.Count);
        Assert.Equal("A", groups[0].Name);
        Assert.Equal("B", groups[1].Name);
    }

    [Fact]
    public void Input_order_is_kept_for_packages_and_singles()
    {
        var groups = PackageDetector.Group(new[] { "one.mkv", "P.part2.rar", "two.mkv", "P.part1.rar" }, s => s);
        Assert.Equal(3, groups.Count);
        Assert.False(groups[0].IsPackage);
        Assert.True(groups[1].IsPackage);
        Assert.Equal(new[] { "P.part1.rar", "P.part2.rar" }, groups[1].Items);
        Assert.False(groups[2].IsPackage);
    }

    [Fact]
    public void Duplicate_parts_stay_in_the_package()
    {
        var g = Assert.Single(PackageDetector.Group(new[] { "P.part1.rar", "P.part2.rar", "P.part2.rar" }, s => s));
        Assert.Equal(3, g.Items.Count);
    }

    [Theory]
    [InlineData(new[] { "F.part1.rar", "F.part3.rar" }, new[] { "part2.rar" })]
    [InlineData(new[] { "F.part2.rar", "F.part3.rar" }, new[] { "part1.rar" })]
    [InlineData(new[] { "F.7z.001", "F.7z.003" }, new[] { "7z.002" })]
    [InlineData(new[] { "F.rar", "F.r00", "F.r02" }, new[] { "r01" })]
    [InlineData(new[] { "F.r00", "F.r01" }, new[] { "rar" })]
    [InlineData(new[] { "F.z01", "F.z03", "F.zip" }, new[] { "z02" })]
    [InlineData(new[] { "F.z01", "F.z02" }, new[] { "zip" })]
    public void Missing_parts_are_named(string[] files, string[] missing)
    {
        var g = Assert.Single(PackageDetector.Group(files, s => s));
        Assert.Equal(missing, g.Missing);
    }

    [Fact]
    public void Group_reads_names_from_any_item_type()
    {
        var items = new[] { ("http://h/a", "X.part1.rar"), ("http://h/b", "X.part2.rar") };
        var g = Assert.Single(PackageDetector.Group(items, i => i.Item2));
        Assert.Equal("http://h/a", g.Items[0].Item1);
    }

    [Theory]
    [InlineData("Film.2020", "Film.2020")]
    [InlineData("Film: Teil 2?", "Film_ Teil 2_")]
    [InlineData("..\\evil", "evil")]
    [InlineData("", "package")]
    [InlineData("CON", "_CON")]
    public void FolderName_is_a_valid_windows_name(string name, string expected)
        => Assert.Equal(expected, PackageDetector.FolderName(name));
}
