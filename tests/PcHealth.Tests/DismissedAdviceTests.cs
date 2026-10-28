using System.Text.RegularExpressions;
using PcHealth.Core.Advice;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// "Don't show again" (STEP-B-SPEC 6): the window remembers a hidden card by part of a fingerprint of
/// what the card says is wrong, in a file of hashes and dates only. A card that gets worse has a new
/// fingerprint and comes back by itself; one that stays the same stays hidden. A corrupt file hides
/// nothing, and only the window ever touches the file.
/// </summary>
public sealed partial class DismissedAdviceTests : IDisposable
{
    private static readonly DateTimeOffset Today = new(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pchealth-dismissed-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_dir, "dismissed-advice.json");

    private DismissedAdviceStore NewStore() => new(FilePath);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Recommendation Card(AdvicePc pc, string item)
    {
        var section = pc.Advice();
        Assert.Empty(section.ItemErrors);
        return Assert.Single(section.Recommendations, c => c.Item == item);
    }

    /// <summary>Whether the card <paramref name="item"/> on this scan is hidden by what the store remembers.</summary>
    private bool IsHidden(AdvicePc pc, string item)
    {
        var section = pc.Advice();
        Assert.Empty(section.ItemErrors);
        var arranged = AdvicePresentation.Arrange(section, NewStore().Load());
        Assert.Contains(arranged.Shown.Concat(arranged.More).Concat(arranged.Hidden), c => c.Item == item);
        return arranged.Hidden.Any(c => c.Item == item);
    }

    // --- The file -----------------------------------------------------------------------------------

    [Fact]
    public void A_hidden_card_is_remembered_by_a_new_store_on_the_same_file()
    {
        var section = AdvicePresentationTests.Cards(3);
        var store = NewStore();

        store.Dismiss(section.Recommendations[0], Today);
        store.Dismiss(section.Recommendations[2], Today);

        Assert.Equal(
            section.Recommendations.Where((_, i) => i != 1).Select(c => AdvicePresentation.DismissKey(c)!).Order(StringComparer.Ordinal),
            NewStore().Load().Order(StringComparer.Ordinal));
        Assert.Equal(["T:1", "T:3"], AdvicePresentation.Arrange(section, NewStore().Load()).Hidden.Select(c => c.Id));
    }

    [Fact]
    public void Hiding_a_card_twice_keeps_one_entry()
    {
        var card = AdvicePresentationTests.Cards(1).Recommendations[0];

        NewStore().Dismiss(card, Today);
        NewStore().Dismiss(card, Today.AddDays(3));

        Assert.Single(NewStore().Load());
    }

    [Fact]
    public void The_file_holds_only_hashes_and_dates_never_a_drive_s_name_or_letter()
    {
        var pc = new AdvicePc().Disk(0, "system", "C:", 104.4).Drive("smart.reallocated", 3).Read("storage.volume.free_pct", 15, "C: (Personal label)");
        var drive = Card(pc, "H1");
        var space = Card(new AdvicePc().Disk(0, "system", "C:", 104.4).Read("storage.volume.free_pct", 15, "C: (Personal label)"), "SPACE");

        NewStore().Dismiss(drive, Today);
        NewStore().Dismiss(space, Today);

        var text = File.ReadAllText(FilePath);
        Assert.Matches(OnlyKeysAndDays(), text);
        var day = Today.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(2, Regex.Count(text, "\"" + day + "\""));
        // Hex digits and dates only, so no model, size unit, letter, label or reading can be in it.
        foreach (var forbidden in new[] { "MODEL", "GB", "C:", "Personal", "storage.", "smart.", "H1", "SPACE" })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
    }

    [GeneratedRegex("""^\{\s*("[0-9A-F]{16}"\s*:\s*"\d{4}-\d{2}-\d{2}"\s*,?\s*)+\}\s*$""")]
    private static partial Regex OnlyKeysAndDays();

    [Fact]
    public void With_no_file_nothing_is_hidden() => Assert.Empty(NewStore().Load());

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"0123456789ABCDEF\"]")]
    [InlineData("{ \"0123456789ABCDEF\": 5 }")]
    [InlineData("{ \"0123456789ABCDEF\": { \"day\": \"2026-10-28\" } }")]
    [InlineData("{ \"T:1\": \"2026-10-28\", \"0123456789ABCDEG\": \"2026-10-28\", \"0123456789ABCDEF0\": \"2026-10-28\" }")]
    public void A_corrupt_file_hides_nothing(string contents)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, contents);

        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void A_good_entry_beside_one_of_the_wrong_shape_is_still_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ \"0123456789abcdef\": \"2026-10-28\", \"T:1\": \"2026-10-28\" }");

        Assert.Equal(["0123456789ABCDEF"], NewStore().Load());
    }

    [Fact]
    public void An_unreadable_file_hides_nothing_and_a_failed_save_is_reported_to_the_caller()
    {
        // A folder where the file should be: reading fails, and so does writing, which the window
        // turns into a line in its status bar.
        Directory.CreateDirectory(FilePath);
        var card = AdvicePresentationTests.Cards(1).Recommendations[0];

        Assert.Empty(NewStore().Load());
        var e = Record.Exception(() => NewStore().Dismiss(card, Today));
        Assert.True(e is IOException or UnauthorizedAccessException, e?.GetType().Name ?? "no exception");
    }

    [Fact]
    public void Hiding_a_card_over_a_corrupt_file_starts_a_clean_one()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "garbage");
        var card = AdvicePresentationTests.Cards(1).Recommendations[0];

        NewStore().Dismiss(card, Today);

        Assert.Equal([AdvicePresentation.DismissKey(card)!], NewStore().Load());
        Assert.Matches(OnlyKeysAndDays(), File.ReadAllText(FilePath));
    }

    [Fact]
    public void Show_again_deletes_the_file()
    {
        var section = AdvicePresentationTests.Cards(2);
        NewStore().Dismiss(section.Recommendations[0], Today);

        NewStore().Clear();

        Assert.False(File.Exists(FilePath));
        Assert.Empty(AdvicePresentation.Arrange(section, NewStore().Load()).Hidden);
    }

    [Fact]
    public void A_list_Show_again_could_not_delete_hides_its_cards_again_at_the_next_scan_and_the_window_says_so()
    {
        // A read-only file: deleting it fails, reading it does not. The window loads the list after
        // every scan, so the cards hide again then, with no restart. Its status line once said "after
        // a restart" (review round, 2026-10-28).
        var section = AdvicePresentationTests.Cards(2);
        NewStore().Dismiss(section.Recommendations[0], Today);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            var e = Record.Exception(() => NewStore().Clear());
            Assert.True(e is IOException or UnauthorizedAccessException, e?.GetType().Name ?? "no exception");
            Assert.Equal(["T:1"], AdvicePresentation.Arrange(section, NewStore().Load()).Hidden.Select(c => c.Id));
        }
        finally
        {
            File.SetAttributes(FilePath, FileAttributes.Normal);
        }

        var window = File.ReadAllText(Path.Combine(AdviceCatalogTests.RepoRoot(), "src", "PcHealth.App", "ViewModels", "MainWindowViewModel.Advice.cs"));
        Assert.Contains("_hiddenAdvice.UnionWith(_dismissedAdvice.Load())", window, StringComparison.Ordinal);
        Assert.Equal("they will hide again after the next scan.", Regex.Match(window, @"could not be deleted \(\{e\.Message\}\), so ([^""]+)""").Groups[1].Value);
        // The other direction: a choice that could not be saved is forgotten at that same next scan.
        Assert.Contains("could not be saved ({e.Message}), so it will show again after the next scan.", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_again_with_nothing_hidden_is_harmless()
    {
        NewStore().Clear();                                   // no folder at all
        Directory.CreateDirectory(_dir);
        NewStore().Clear();                                   // a folder, no file

        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void A_card_without_a_fingerprint_cannot_be_hidden()
    {
        var card = AdvicePresentationTests.Cards(1).Recommendations[0] with { Fingerprint = "" };

        Assert.Throws<ArgumentException>(() => NewStore().Dismiss(card, Today));
        Assert.False(File.Exists(FilePath));
    }

    // --- A hidden card stays hidden while nothing changes, and comes back when it gets worse ------------

    private static AdvicePc FailingSsd(double reallocated) =>
        new AdvicePc().Disk(0, "system", "C:", 104.4).Drive("smart.reallocated", reallocated);

    [Fact]
    public void A_hidden_drive_card_stays_hidden_on_the_same_scan_and_when_an_unrelated_reading_moves()
    {
        NewStore().Dismiss(Card(FailingSsd(3), "H1"), Today);

        Assert.True(IsHidden(FailingSsd(3), "H1"));
        Assert.True(IsHidden(FailingSsd(3).Drive("smart.temperature_c", 44).Read("os.uptime_days", 2), "H1"));
    }

    [Fact]
    public void A_hidden_drive_card_comes_back_when_its_count_rises_even_with_the_same_grade()
    {
        // With history off (the kit, --no-history) no growth trend is made, so the count itself is in
        // the fingerprint: 3 retired areas becoming 4 is still "check again", and still news.
        NewStore().Dismiss(Card(FailingSsd(3), "H1"), Today);

        Assert.False(IsHidden(FailingSsd(4), "H1"));
    }

    [Fact]
    public void A_hidden_drive_card_comes_back_when_it_turns_from_check_again_to_act_now()
    {
        NewStore().Dismiss(Card(FailingSsd(3), "H1"), Today);

        Assert.False(IsHidden(FailingSsd(5), "H1"));
    }

    [Fact]
    public void A_hidden_drive_card_comes_back_with_a_growth_trend()
    {
        NewStore().Dismiss(Card(FailingSsd(3), "H1"), Today);

        Assert.False(IsHidden(FailingSsd(3).Growth("smart.reallocated", 2, 3), "H1"));
    }

    [Fact]
    public void A_hidden_drive_card_comes_back_with_a_new_reading_behind_it()
    {
        // Already "act now", so the urgency does not move: the new evidence alone brings it back.
        NewStore().Dismiss(Card(FailingSsd(5), "H1"), Today);

        Assert.True(IsHidden(FailingSsd(5), "H1"));
        Assert.False(IsHidden(FailingSsd(5).Drive("smart.pending_sectors", 1), "H1"));
    }

    [Fact]
    public void A_hidden_space_card_stays_hidden_within_its_grade_and_comes_back_when_it_turns_Bad()
    {
        static AdvicePc Free(double pct) => new AdvicePc().Disk(0, "system", "C:", 104.4).Read("storage.volume.free_pct", pct, "C:");
        NewStore().Dismiss(Card(Free(15), "SPACE"), Today);

        Assert.True(IsHidden(Free(15), "SPACE"));
        Assert.True(IsHidden(Free(12), "SPACE"));   // still Normal: no new card for every point lost
        Assert.False(IsHidden(Free(8), "SPACE"));   // Bad
    }

    // --- Who touches the file -----------------------------------------------------------------------

    [Fact]
    public void Only_the_window_reads_or_writes_the_file()
    {
        // The headless scan, the elevated helper and the kit never touch it, so a kit run still
        // leaves nothing on the laptop and no surface but the window hides a card.
        var root = AdviceCatalogTests.RepoRoot();
        var users = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(nameof(DismissedAdviceStore), StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        Assert.Equal(
        [
            "src/PcHealth.App/ViewModels/MainWindowViewModel.Advice.cs",
            "src/PcHealth.Core/Advice/DismissedAdviceStore.cs",
        ], users);
        foreach (var script in Directory.GetFiles(Path.Combine(root, "audit-kit"), "*.ps1"))
            Assert.DoesNotContain("dismissed-advice", File.ReadAllText(script), StringComparison.OrdinalIgnoreCase);
    }
}
