using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Xunit;
using YDownloader.Packages;

namespace YDownloader.Tests;

public class ArchivePasswordTests
{
    // Ausgedachte Passwörter, keine echten.
    [Theory]
    [InlineData("geheim123", "geheim123")]
    [InlineData("  mit Rand  ", "mit Rand")]
    [InlineData("Grüße ß €", "Grüße ß €")]
    [InlineData("zwei Wörter", "zwei Wörter")]
    public void Clean_keeps_valid_passwords(string input, string expected) => Assert.Equal(expected, PasswordRules.Clean(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    public void Clean_rejects_invalid_passwords(string? input) => Assert.Null(PasswordRules.Clean(input));

    [Fact]
    public void Clean_rejects_too_long_but_accepts_max_length()
    {
        Assert.NotNull(PasswordRules.Clean(new string('x', PasswordRules.MaxLength)));
        Assert.Null(PasswordRules.Clean(new string('x', PasswordRules.MaxLength + 1)));
    }

    [Fact]
    public void Normalize_drops_empty_and_duplicates_and_keeps_order()
    {
        var r = PasswordRules.Normalize(new[] { "b", "a", " b ", "", null, "A", "a" });
        Assert.Equal(new[] { "b", "a", "A" }, r); // Groß-/Kleinschreibung zählt
    }

    [Fact]
    public void Normalize_caps_at_fifty()
    {
        var r = PasswordRules.Normalize(Enumerable.Range(0, 80).Select(i => "pw" + i));
        Assert.Equal(PasswordRules.MaxCandidates, r.Count);
        Assert.Equal("pw0", r[0]);
        Assert.Equal("pw49", r[^1]);
    }

    [Fact]
    public void ParseLines_reads_one_password_per_line()
    {
        var r = PasswordRules.ParseLines("eins\r\nzwei\n\n  drei  \r\neins\n");
        Assert.Equal(new[] { "eins", "zwei", "drei" }, r);
        Assert.Empty(PasswordRules.ParseLines(null));
        Assert.Empty(PasswordRules.ParseLines("\n \r\n"));
    }

    [Fact]
    public void Plan_starts_without_password_then_entered_package_list()
    {
        var plan = PasswordRules.Plan(new[] { "eingegeben" }, new[] { "paket" }, new[] { "liste1", "liste2" });
        Assert.Equal(new string?[] { null, "eingegeben", "paket", "liste1", "liste2" }, plan);
    }

    [Fact]
    public void Plan_drops_duplicates_keeping_the_earliest_position()
    {
        var plan = PasswordRules.Plan(new[] { "x" }, new[] { "liste1", "x" }, new[] { "liste1", "y" });
        Assert.Equal(new string?[] { null, "x", "liste1", "y" }, plan);
    }

    [Fact]
    public void Plan_with_nothing_is_just_the_attempt_without_password()
    {
        Assert.Equal(new string?[] { null }, PasswordRules.Plan(null, null, null));
    }

    [Fact]
    public void Plan_never_exceeds_fifty_passwords_and_keeps_the_front_ones()
    {
        var list = Enumerable.Range(0, 70).Select(i => "l" + i);
        var plan = PasswordRules.Plan(new[] { "neu" }, new[] { "paket" }, list);
        Assert.Equal(1 + PasswordRules.MaxCandidates, plan.Count);
        Assert.Null(plan[0]);
        Assert.Equal("neu", plan[1]);
        Assert.Equal("paket", plan[2]);
        Assert.Equal("l47", plan[^1]);
    }

    // ---------------------------------------------------------------- Ablage

    [Fact]
    public void Encode_decode_roundtrip_without_plaintext()
    {
        string? cipher = PasswordRules.Encode(new[] { "geheim123", "Grüße ß" });
        Assert.NotNull(cipher);
        Assert.DoesNotContain("geheim123", cipher);
        Assert.Equal(new[] { "geheim123", "Grüße ß" }, PasswordRules.Decode(cipher));
    }

    [Fact]
    public void Encode_of_nothing_is_null_and_decode_of_garbage_is_empty()
    {
        Assert.Null(PasswordRules.Encode(Array.Empty<string>()));
        Assert.Null(PasswordRules.Encode(new[] { "", "  " }));
        Assert.Empty(PasswordRules.Decode(null));
        Assert.Empty(PasswordRules.Decode("kein base64 !!"));
        Assert.Empty(PasswordRules.Decode("AAAA")); // gültiges Base64, aber keine DPAPI-Chiffre
    }

    [Fact]
    public void Settings_json_holds_only_cipher()
    {
        var s = new AppSettings();
        s.ArchivePasswords.Passwords = new List<string> { "geheim123", "zweites" };
        string json = System.Text.Encoding.UTF8.GetString(s.ToJson());
        Assert.DoesNotContain("geheim123", json);
        Assert.DoesNotContain("zweites", json);
        Assert.Contains("protectedList", json);

        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(new[] { "geheim123", "zweites" }, back.ArchivePasswords.Passwords);
        Assert.False(back.ArchivePasswords.Unreadable);
    }

    [Fact]
    public void Empty_list_writes_nothing()
    {
        var s = new AppSettings();
        s.ArchivePasswords.Passwords = new List<string>();
        Assert.DoesNotContain("protectedList", System.Text.Encoding.UTF8.GetString(s.ToJson()));
        Assert.False(s.ArchivePasswords.HasList);
    }

    [Fact]
    public void Unreadable_cipher_is_reported_and_gives_empty_list()
    {
        var s = new ArchivePasswordSettings { ProtectedList = "AAAA" };
        Assert.True(s.HasList);
        Assert.True(s.Unreadable);
        Assert.Empty(s.Passwords);
    }

    [Fact]
    public void Remember_adds_new_ignores_known_and_respects_the_limit()
    {
        var (list, added) = PasswordRules.Remember(new[] { "a" }, " b ");
        Assert.True(added);
        Assert.Equal(new[] { "a", "b" }, list);

        (list, added) = PasswordRules.Remember(list, "a");
        Assert.False(added);
        Assert.Equal(2, list.Count);

        (list, added) = PasswordRules.Remember(list, "  ");
        Assert.False(added);

        var full = Enumerable.Range(0, PasswordRules.MaxCandidates).Select(i => "p" + i).ToList();
        (list, added) = PasswordRules.Remember(full, "neu");
        Assert.False(added);
        Assert.Equal(PasswordRules.MaxCandidates, list.Count);
    }

    // ---------------------------------------------------------------- Suche

    private static Attempt Wrong => new(AttemptOutcome.WrongPassword);

    [Fact]
    public void Search_stops_at_first_success_and_reports_password_and_count()
    {
        var seen = new List<string?>();
        var plan = new string?[] { null, "a", "b", "c", "d" };
        var r = PasswordSearch.Run(plan, pw =>
        {
            seen.Add(pw);
            return pw == "b" ? new Attempt(AttemptOutcome.Success, new ExtractResult(ExtractState.Done)) : Wrong;
        }, CancellationToken.None);

        Assert.Equal(new string?[] { null, "a", "b" }, seen);
        Assert.Equal("b", r.Password);
        Assert.Equal(2, r.Tried); // der Versuch ohne Passwort zählt nicht
        Assert.Equal(ExtractState.Done, r.Last.Result!.State);
    }

    [Fact]
    public void Search_without_password_success_has_no_password_and_tries_none()
    {
        var r = PasswordSearch.Run(new string?[] { null, "a" }, pw => new Attempt(AttemptOutcome.Success), CancellationToken.None);
        Assert.Null(r.Password);
        Assert.Equal(0, r.Tried);
    }

    [Fact]
    public void Search_tries_all_when_nothing_fits()
    {
        var plan = new string?[] { null, "a", "b", "c" };
        int calls = 0;
        var r = PasswordSearch.Run(plan, _ => { calls++; return Wrong; }, CancellationToken.None);
        Assert.Equal(4, calls);
        Assert.Equal(3, r.Tried);
        Assert.Null(r.Password);
        Assert.Equal(AttemptOutcome.WrongPassword, r.Last.Outcome);
    }

    [Fact]
    public void Search_stops_on_other_errors_without_trying_more()
    {
        var seen = new List<string?>();
        var stop = new Attempt(AttemptOutcome.Stop, new ExtractResult(ExtractState.Failed, ExtractFailure.NoSpace));
        var r = PasswordSearch.Run(new string?[] { null, "a", "b" }, pw => { seen.Add(pw); return pw == "a" ? stop : Wrong; }, CancellationToken.None);
        Assert.Equal(new string?[] { null, "a" }, seen);
        Assert.Equal(ExtractFailure.NoSpace, r.Last.Result!.Failure);
        Assert.Equal(1, r.Tried);
    }

    [Fact]
    public void Search_never_tries_more_than_fifty_passwords_even_with_a_long_plan()
    {
        var plan = new List<string?> { null };
        plan.AddRange(Enumerable.Range(0, 80).Select(i => "p" + i));
        int calls = 0;
        var r = PasswordSearch.Run(plan, _ => { calls++; return Wrong; }, CancellationToken.None);
        Assert.Equal(1 + PasswordRules.MaxCandidates, calls);
        Assert.Equal(PasswordRules.MaxCandidates, r.Tried);
    }

    [Fact]
    public void Search_honours_cancellation_between_attempts()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() =>
            PasswordSearch.Run(new string?[] { null, "a", "b" }, _ => { calls++; cts.Cancel(); return Wrong; }, cts.Token));
        Assert.Equal(1, calls);
    }
}
