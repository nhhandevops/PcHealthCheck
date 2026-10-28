using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcHealth.Core.Advice;
using PcHealth.Core.Reporting;

namespace PcHealth.App.ViewModels;

/// <summary>
/// "What you can do": the report's advice section, laid out by <see cref="AdvicePresentation"/> like
/// the HTML report, with one thing only the window has: "Don't show again".
///
/// A hidden card is remembered by a fingerprint of what it says is wrong, so it comes back by itself
/// when that gets worse, and the hidden line says when an "Act now" card is among the hidden ones.
/// Exported reports always list every card. An elevated window never hides one: it may be running
/// as whoever typed the administrator password, whose choice must not hide the employee's cards,
/// and it writes nothing into that account's profile.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly DismissedAdviceStore _dismissedAdvice = new();
    private readonly HashSet<string> _hiddenAdvice = new(StringComparer.Ordinal);
    private AdviceSection? _advice;
    private IReadOnlyList<ModuleRunSummary> _adviceModules = [];

    public ObservableCollection<AdviceCardViewModel> AdviceCards { get; } = [];

    public ObservableCollection<AdviceCardViewModel> MoreAdviceCards { get; } = [];

    [ObservableProperty] private bool _hasAdvice;
    [ObservableProperty] private string _adviceTitle = "";
    [ObservableProperty] private string _adviceIntro = "";
    [ObservableProperty] private string _moreAdviceHeader = "";
    [ObservableProperty] private bool _hasMoreAdvice;
    [ObservableProperty] private string _notMeasuredText = "";
    [ObservableProperty] private bool _hasNotMeasured;
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private bool _hasNotes;
    [ObservableProperty] private string _hiddenAdviceText = "";
    [ObservableProperty] private string _showHiddenAdviceLabel = "";
    [ObservableProperty] private bool _hasHiddenAdvice;
    [ObservableProperty] private string _adviceEmptyText = "";
    [ObservableProperty] private bool _hasAdviceEmptyText;
    [ObservableProperty] private string _otherFindingsText = "";
    [ObservableProperty] private bool _hasOtherFindings;

    /// <summary>Shows the section of a finished scan, hiding the cards this account chose not to see again.</summary>
    private void PresentAdvice(ScanReport report)
    {
        _advice = report.Advice;
        _adviceModules = report.Modules;
        _hiddenAdvice.Clear();
        if (_advice is not null && !_alreadyElevated) _hiddenAdvice.UnionWith(_dismissedAdvice.Load());
        ArrangeAdvice();
    }

    /// <summary>Empties the section when a new scan starts, as the scan does with the other results.</summary>
    private void ClearAdvice()
    {
        _advice = null;
        _adviceModules = [];
        _hiddenAdvice.Clear();
        ArrangeAdvice();
    }

    private void ArrangeAdvice()
    {
        AdviceCards.Clear();
        MoreAdviceCards.Clear();
        HasAdvice = _advice is not null;
        if (_advice is null) return;

        var view = AdvicePresentation.View(_advice, _adviceModules, _hiddenAdvice);
        foreach (var card in view.Shown) AdviceCards.Add(CardOf(card, view));
        foreach (var card in view.More) MoreAdviceCards.Add(CardOf(card, view));

        AdviceTitle = view.Title;
        AdviceIntro = view.Intro;
        MoreAdviceHeader = view.MoreHeader ?? "";
        HasMoreAdvice = view.MoreHeader is not null;
        // Amber: a check that tried and did not finish, or advice that could not be worked out.
        NotMeasuredText = string.Join("\n", view.NotMeasured.Concat(view.Errors));
        HasNotMeasured = NotMeasuredText.Length > 0;
        // Grey: deliberately not part of this scan, which says nothing is wrong with it.
        NotesText = string.Join("\n", view.Notes);
        HasNotes = NotesText.Length > 0;
        HiddenAdviceText = view.HiddenLine ?? "";
        ShowHiddenAdviceLabel = view.ShowHiddenLabel;
        HasHiddenAdvice = view.HiddenLine is not null;
        AdviceEmptyText = string.Join(" ", view.EmptyState);
        HasAdviceEmptyText = AdviceEmptyText.Length > 0;
        OtherFindingsText = view.OtherFindings ?? "";
        HasOtherFindings = view.OtherFindings is not null;
    }

    private AdviceCardViewModel CardOf(AdviceCardView card, AdviceView view) =>
        new(card, view.DontShowLabel,
            canDismiss: !_alreadyElevated && AdvicePresentation.DismissKey(card.Card) is not null,
            DismissAdviceCommand);

    [RelayCommand]
    private void DismissAdvice(AdviceCardViewModel? card)
    {
        if (card is null || _alreadyElevated || AdvicePresentation.DismissKey(card.Card) is not { } key) return;

        _hiddenAdvice.Add(key);
        ArrangeAdvice();
        try
        {
            _dismissedAdvice.Dismiss(card.Card, DateTimeOffset.Now);
            ProgressText = "Card hidden. It comes back by itself if it gets worse; exported reports still list it.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ProgressText = $"Card hidden for now, but the choice could not be saved ({e.Message}), so it will show again after the next scan.";
        }
    }

    [RelayCommand]
    private void ShowHiddenAdvice()
    {
        _hiddenAdvice.Clear();
        ArrangeAdvice();
        try
        {
            _dismissedAdvice.Clear();
            ProgressText = "Every hidden card is shown again.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ProgressText = $"The hidden cards are shown, but the list of them could not be deleted ({e.Message}), so they will hide again after the next scan.";
        }
    }
}

/// <summary>One "What you can do" card in the window.</summary>
public sealed class AdviceCardViewModel(AdviceCardView card, string dontShowLabel, bool canDismiss, ICommand dismiss)
{
    public Recommendation Card { get; } = card.Card;

    /// <summary>Coloured by the worst grade behind the card, like every other chip in the window.</summary>
    public IBrush UrgencyBrush { get; } = GradeVisuals.Brush(card.Card.Severity);

    public string UrgencyLabel { get; } = card.Urgency.ToUpperInvariant();
    public string Title { get; } = card.Title;
    public string Subject { get; } = card.Subject ?? "";
    public bool HasSubject { get; } = !string.IsNullOrEmpty(card.Subject);
    public IReadOnlyList<AdviceLineViewModel> Lines { get; } = [.. card.Lines.Select(l => new AdviceLineViewModel(l))];
    public string DontShowLabel { get; } = dontShowLabel;
    public bool CanDismiss { get; } = canDismiss;
    public ICommand DismissCommand { get; } = dismiss;
}

/// <summary>A line of a card. A line for IT carries a "FOR IT" badge, so an employee knows to pass it on.</summary>
public sealed class AdviceLineViewModel(AdviceLineView line)
{
    public string Text { get; } = line.Text;
    public bool IsForIt { get; } = line.Label is not null;
    public string ForItLabel { get; } = (line.Label ?? "").TrimEnd(':').ToUpperInvariant();
}
