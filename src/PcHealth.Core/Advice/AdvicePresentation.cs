using System.Collections.Frozen;
using System.Globalization;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;

namespace PcHealth.Core.Advice;

/// <summary>Which cards a surface shows: the first ones up to its cap, the rest behind "N more", and the ones hidden.</summary>
public sealed record AdviceArrangement(
    IReadOnlyList<Recommendation> Shown,
    IReadOnlyList<Recommendation> More,
    IReadOnlyList<Recommendation> Hidden);

/// <summary>One line of a card as it is shown. <see cref="Label"/> is "For IT:" on a line for IT to act on, and null otherwise.</summary>
public sealed record AdviceLineView(AdviceLineRole Role, string Text, string? Label);

/// <summary>One card as it is shown: its urgency in words, its title, the drive it is about, and its lines.</summary>
public sealed record AdviceCardView(Recommendation Card, string Urgency, string Title, string? Subject, IReadOnlyList<AdviceLineView> Lines);

/// <summary>
/// The "What you can do" section in words, ready for a surface to lay out. The window and the HTML
/// report show these parts in this order: the cards, "N more", the not-measured lines and the item
/// errors (amber), the notes (grey), the hidden line, then the empty state and the other-findings line.
/// </summary>
/// <param name="MoreHeader">"2 more", or null when nothing is behind it.</param>
/// <param name="HiddenLine">"2 hidden", or "1 hidden, marked Act now"; null when nothing is hidden.</param>
/// <param name="Errors">An item that broke, or the section itself when it could not be put into words.</param>
/// <param name="EmptyState">Only when there is no card at all: "Nothing to suggest…", and when a check did not finish, that this is not an all-clear.</param>
public sealed record AdviceView(
    string Title,
    string Intro,
    IReadOnlyList<AdviceCardView> Shown,
    IReadOnlyList<AdviceCardView> More,
    string? MoreHeader,
    IReadOnlyList<AdviceCardView> Hidden,
    string? HiddenLine,
    IReadOnlyList<string> NotMeasured,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> EmptyState,
    string? OtherFindings,
    string DontShowLabel,
    string ShowHiddenLabel);

/// <summary>
/// How the "What you can do" section is laid out, the same way on every surface (STEP-B-SPEC 3.6
/// and 6). Pure, so it is tested here; the test project has no Avalonia tests.
///
/// At most <see cref="Cap"/> cards are shown; the rest are behind "N more", and every cap says what
/// it dropped. Cards hidden with "Don't show again" are taken out before the cap and counted, and
/// only the window hides any: the HTML report and the JSON list every card, and the headless console
/// the first <see cref="Cap"/> and how many more, so a report sent to IT is never thinned by one
/// user's choice. Not-measured lines, notes, item errors and the other-findings line never count
/// toward the cap.
/// </summary>
public static class AdvicePresentation
{
    /// <summary>The most cards shown before "N more" (REMEDIES-PLAN principle 14).</summary>
    public const int Cap = 5;

    /// <summary>How many hex digits of a card's fingerprint "Don't show again" keeps.</summary>
    public const int DismissKeyLength = 16;

    /// <summary>The hidden set for every surface but the window.</summary>
    public static IReadOnlySet<string> NothingHidden { get; } = FrozenSet<string>.Empty;

    /// <summary>
    /// The key "Don't show again" remembers a card by: the first <see cref="DismissKeyLength"/> hex
    /// digits of its fingerprint. A card made without a fingerprint has none and cannot be hidden.
    /// </summary>
    public static string? DismissKey(Recommendation card) =>
        card.Fingerprint.Length >= DismissKeyLength ? card.Fingerprint[..DismissKeyLength] : null;

    /// <summary>
    /// Splits the section's cards, kept in its display order, into those shown, those behind "N more"
    /// and those hidden. <paramref name="hiddenKeys"/> holds <see cref="DismissKey"/>s. A hidden card
    /// does not count toward <paramref name="cap"/>.
    /// </summary>
    public static AdviceArrangement Arrange(AdviceSection section, IReadOnlySet<string> hiddenKeys, int cap = Cap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        var hidden = new List<Recommendation>();
        var visible = new List<Recommendation>();
        foreach (var card in section.Recommendations)
        {
            (DismissKey(card) is { } key && hiddenKeys.Contains(key) ? hidden : visible).Add(card);
        }
        return new AdviceArrangement([.. visible.Take(cap)], [.. visible.Skip(cap)], hidden);
    }

    /// <summary>
    /// The section in words. Sentences are rendered again from their key and arguments, with
    /// <paramref name="catalog"/> (English when null), so Step E only has to pass another one.
    ///
    /// It never throws. The engine turns a broken catalog into an item error rather than failing the
    /// scan, and a surface that then failed on the same catalog would undo that: the headless scan
    /// would crash before writing its report. So a section that cannot be put into words comes back
    /// as one English line saying so, with the error's type only.
    /// </summary>
    public static AdviceView View(
        AdviceSection section,
        IReadOnlyList<ModuleRunSummary> modules,
        IReadOnlySet<string> hiddenKeys,
        int cap = Cap,
        AdviceCatalog? catalog = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        try
        {
            return Build(section, modules, hiddenKeys, cap, catalog ?? AdviceCatalog.English);
        }
        catch (Exception e)
        {
            return new AdviceView("What you can do", "", [], [], null, [], null, [], [],
                ["This section could not be put into words (" + e.GetType().Name + "). The readings below are complete."],
                [], null, "Don't show again", "Show again");
        }
    }

    private static AdviceView Build(
        AdviceSection section, IReadOnlyList<ModuleRunSummary> modules, IReadOnlySet<string> hiddenKeys, int cap, AdviceCatalog catalog)
    {
        var (shown, more, hidden) = Arrange(section, hiddenKeys, cap);
        var forIt = Say(catalog, "advice.section.for-it");
        AdviceCardView Card(Recommendation card) => new(
            card,
            Say(catalog, UrgencyKey(card.Urgency)),
            Say(catalog, card.Title),
            card.Subject,
            [.. card.Lines.Select(l => new AdviceLineView(l.Role, Say(catalog, l), l.Role == AdviceLineRole.ForIt ? forIt : null))]);

        var incomplete = IncompleteCount(modules);
        IReadOnlyList<string> empty = section.Recommendations.Count > 0
            ? []
            : incomplete == 0
                ? [Say(catalog, "advice.section.empty")]
                : [Say(catalog, "advice.section.empty"), Say(catalog, "advice.section.incomplete", ("count", AdviceArg.Count(incomplete)))];

        return new AdviceView(
            Title: Say(catalog, "advice.section.title"),
            Intro: Say(catalog, "advice.section.intro"),
            Shown: [.. shown.Select(Card)],
            More: [.. more.Select(Card)],
            MoreHeader: more.Count > 0 ? Say(catalog, "advice.section.more", ("count", AdviceArg.Count(more.Count))) : null,
            Hidden: [.. hidden.Select(Card)],
            HiddenLine: hidden.Count == 0
                ? null
                : Say(catalog, hidden.Any(c => c.Urgency == AdviceUrgency.ActNow) ? "advice.section.hidden-act-now" : "advice.section.hidden",
                    ("count", AdviceArg.Count(hidden.Count))),
            NotMeasured: [.. section.NotMeasured.Select(n => Say(catalog, n.Text))],
            Notes: [.. section.Notes.Select(n => Say(catalog, n))],
            Errors: [.. section.ItemErrors.Select(e => Say(catalog, e))],
            EmptyState: empty,
            OtherFindings: section.OtherFindings > 0
                ? Say(catalog, "advice.section.other-findings", ("count", AdviceArg.Count(section.OtherFindings)))
                : null,
            DontShowLabel: Say(catalog, "advice.section.dont-show"),
            ShowHiddenLabel: Say(catalog, "advice.section.show-hidden"));
    }

    /// <summary>
    /// The block the headless scan prints, in English: the console is data, and the kit shows it
    /// (REMEDIES-PLAN decision 6). Up to <see cref="Cap"/> titles, then how many more the report holds;
    /// no card is ever hidden here. It is printed above the list of checks, so "listed below" is where
    /// the console names the checks that did not finish.
    /// </summary>
    public static IReadOnlyList<string> ConsoleLines(AdviceSection section, IReadOnlyList<ModuleRunSummary> modules)
    {
        var view = View(section, modules, NothingHidden);
        var lines = new List<string>();
        var count = view.Shown.Count + view.More.Count;
        if (count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"What you can do ({count}):"));
            for (var i = 0; i < view.Shown.Count; i++)
            {
                var card = view.Shown[i];
                var subject = card.Subject is { Length: > 0 } s ? " (" + s + ")" : "";
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {i + 1}. [{card.Urgency}] {card.Title}{subject}"));
            }
            if (view.More.Count > 0)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"  … and {view.More.Count} more in the report."));
        }
        else if (section.Recommendations.Count == 0 && view.EmptyState.Count > 0)
        {
            lines.Add("What you can do: nothing to suggest from the checks that ran.");
            var incomplete = IncompleteCount(modules);
            if (incomplete > 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  {incomplete} {(incomplete == 1 ? "check" : "checks")} did not finish (listed below), so this is not an all-clear."));
            }
        }
        else
        {
            lines.Add("What you can do:");
        }

        lines.AddRange(view.NotMeasured.Select(t => "  Not measured: " + t));
        lines.AddRange(view.Notes.Select(t => "  Note: " + t));
        lines.AddRange(view.Errors.Select(t => "  " + t));
        if (section.OtherFindings == 1)
            lines.Add("  1 other reading is graded Normal or Action needed; see the report.");
        else if (section.OtherFindings > 1)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {section.OtherFindings} other readings are graded Normal or Action needed; see the report."));
        return lines;
    }

    /// <summary>The checks the report's banner lists: those that tried and did not finish.</summary>
    private static int IncompleteCount(IReadOnlyList<ModuleRunSummary> modules) =>
        modules.Count(m => m.Status is ModuleStatus.Failed or ModuleStatus.PartiallyCompleted);

    private static string UrgencyKey(AdviceUrgency urgency) => urgency switch
    {
        AdviceUrgency.ActNow => "advice.urgency.act-now",
        AdviceUrgency.CheckAgain => "advice.urgency.check-again",
        AdviceUrgency.Plan => "advice.urgency.plan",
        _ => "advice.urgency.advice",
    };

    /// <summary>A sentence of the section's own, which always has its arguments.</summary>
    private static string Say(AdviceCatalog catalog, string key, params (string Name, AdviceArg Value)[] args) =>
        catalog.Text(AdviceLineRole.Finding, key, args)?.Text
        ?? throw new InvalidDataException($"The catalog's {key} names an argument the section does not give.");

    /// <summary>A card's sentence, from its key and arguments; its English text when the catalog cannot render it.</summary>
    private static string Say(AdviceCatalog catalog, AdviceText text) =>
        catalog.Contains(text.Key) && catalog.Render(text.Key, text.Args) is { } rendered ? rendered : text.Text;
}
