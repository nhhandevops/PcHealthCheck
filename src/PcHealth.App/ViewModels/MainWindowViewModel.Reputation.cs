using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcHealth.App.Views;
using PcHealth.Net.Optional;
using PcHealth.Net.Optional.VirusTotal;
using PcHealth.Modules.Windows.Startup;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.App.ViewModels;

/// <summary>
/// The one feature in this app that uses the internet: asking VirusTotal what seventy
/// antivirus engines make of the programs that start with Windows.
///
/// Everything about it is deliberately awkward on purpose. It never runs as part of a scan.
/// It needs the user's own API key, typed in each session and never written to disk. And the
/// consent dialog lists every fingerprint that would leave the machine, because sending a
/// hash tells VirusTotal this PC holds that exact file — the only disclosure this app is
/// ever capable of making.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _reputationCts;

    public ObservableCollection<ReputationRowViewModel> ReputationResults { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckReputationCommand), nameof(CancelReputationCheckCommand))]
    private bool _isReputationCheckRunning;

    [ObservableProperty] private string _virusTotalApiKey = "";
    [ObservableProperty] private string _reputationHeadline = "";
    [ObservableProperty] private bool _hasReputationResults;

    private bool CanCheckReputation() => !IsReputationCheckRunning && !IsScanning;

    private bool CanCancelReputationCheck() => IsReputationCheckRunning;

    [RelayCommand(CanExecute = nameof(CanCheckReputation))]
    private async Task CheckReputationAsync()
    {
        if (ConfirmInteraction is null) return;

        var apiKey = VirusTotalApiKey.Trim();
        if (apiKey.Length == 0)
        {
            ProgressText = "Paste your own VirusTotal API key first — it is free, from your account page at " +
                           "virustotal.com. The key stays in memory for this session only and is never saved to disk.";
            return;
        }

        var notes = new List<string>();
        var items = await Task.Run(() => StartupInventory.Collect(notes));
        var (selected, skipped) = ReputationSelection.Choose(items
            .Where(i => !i.Disabled && i.Sha256 is not null && i.ResolvedPath is not null)
            .Select(i => new ReputationCandidate(i.Name, i.ResolvedPath!, i.Sha256!, TrustOf(i.Signature))));

        if (selected.Count == 0)
        {
            ProgressText = "No startup item resolved to a file that could be fingerprinted, so there is nothing to look up.";
            return;
        }

        var approved = await ConfirmInteraction(new ConfirmRequest(
            Title: "Send these fingerprints to VirusTotal?",
            Message: "This is the only part of the app that uses the internet.\n\n" +
                     "What leaves this PC: a SHA-256 fingerprint per file, and nothing else. The files " +
                     "themselves are never uploaded, so their contents stay here — but a fingerprint does tell " +
                     "VirusTotal that this machine holds that exact file.\n\n" +
                     ReputationSelection.DescribeForConsent(selected, skipped) + "\n\n" +
                     "Your API key is sent to VirusTotal to authenticate the lookups and is not stored anywhere.",
            CommandText: $"GET https://{VirusTotalClient.Host}/api/v3/files/<sha256>  " +
                         $"× {selected.Count}, one every {VirusTotalClient.FreeTierInterval.TotalSeconds:0} seconds",
            YesLabel: "Look them up",
            NoLabel: "Don't send anything"));

        if (!approved)
        {
            ProgressText = "Nothing was sent. No network connection was made.";
            return;
        }

        IsReputationCheckRunning = true;
        _reputationCts = new CancellationTokenSource();
        ReputationResults.Clear();
        HasReputationResults = false;

        try
        {
            var progress = new Progress<string>(text => ProgressText = text);
            var results = await new ReputationCheck(apiKey)
                .RunAsync(selected, progress, _reputationCts.Token);

            foreach (var result in results.OrderBy(RankOf)) ReputationResults.Add(new ReputationRowViewModel(result));
            ReputationHeadline = ReputationSummary.Describe(results);
            HasReputationResults = true;
            ProgressText = $"Reputation check finished. {NetworkActivityLog.Count} lookup(s) have been made this session.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = $"Reputation check stopped. {NetworkActivityLog.Count} lookup(s) had already been sent; " +
                           "the rest of the files were not checked.";
        }
        finally
        {
            _reputationCts?.Dispose();
            _reputationCts = null;
            IsReputationCheckRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelReputationCheck))]
    private void CancelReputationCheck() => _reputationCts?.Cancel();

    private static SignatureTrust TrustOf(AuthenticodeVerifier.SignatureState state) => state switch
    {
        AuthenticodeVerifier.SignatureState.SignedAndValid => SignatureTrust.Verified,
        AuthenticodeVerifier.SignatureState.NotSigned => SignatureTrust.NoSignature,
        AuthenticodeVerifier.SignatureState.Invalid => SignatureTrust.BrokenSignature,
        _ => SignatureTrust.Unknown,
    };

    private static int RankOf(FileReputation result) => result.Verdict switch
    {
        ReputationVerdict.Malicious => 0,
        ReputationVerdict.Suspicious => 1,
        ReputationVerdict.NotKnown => 2,
        ReputationVerdict.LookupFailed => 3,
        _ => 4,
    };
}

public sealed class ReputationRowViewModel(FileReputation result)
{
    public string Label { get; } = result.Verdict switch
    {
        ReputationVerdict.Malicious => "MALICIOUS",
        ReputationVerdict.Suspicious => "CHECK THIS",
        ReputationVerdict.NotKnown => "UNKNOWN",
        ReputationVerdict.LookupFailed => "NOT CHECKED",
        _ => "CLEAN",
    };

    public IBrush Brush { get; } = result.Verdict switch
    {
        ReputationVerdict.Malicious => new SolidColorBrush(Color.Parse("#c62828")),
        ReputationVerdict.Suspicious => new SolidColorBrush(Color.Parse("#b8860b")),
        ReputationVerdict.Clean => new SolidColorBrush(Color.Parse("#1a7f37")),
        _ => new SolidColorBrush(Color.Parse("#607080")),
    };

    public string Name { get; } = result.DisplayName;
    public string Summary { get; } = result.Summary;
    public string Fingerprint { get; } = result.Sha256.ToLowerInvariant();
}
