using PcHealth.Core.Dependencies;
using Xunit;

namespace PcHealth.Tests;

public sealed class DependencyLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pchealth-deps-{Guid.NewGuid():N}");

    private DependencyLedger NewLedger() => new(_dir);

    private static InstalledDependency PawnIo(DateTimeOffset? at = null) => new(
        "pawnio", "PawnIO sensor driver", at ?? DateTimeOffset.Now,
        "winget install namazso.PawnIO", "winget install namazso.PawnIO  (or download from pawnio.eu)");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Recording_an_install_updates_state_and_writes_history()
    {
        var ledger = NewLedger();
        ledger.RecordInstalled(PawnIo());

        var state = ledger.LoadInstalledByApp();
        Assert.True(state.ContainsKey("pawnio"));

        var history = ledger.ReadHistory();
        var line = Assert.Single(history);
        Assert.Contains("INSTALLED PawnIO sensor driver", line, StringComparison.Ordinal);
        Assert.Contains("winget install namazso.PawnIO", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Recording_a_removal_clears_state_and_logs_the_reinstall_command()
    {
        var ledger = NewLedger();
        var record = PawnIo(DateTimeOffset.Now.AddDays(-3));
        ledger.RecordInstalled(record);
        ledger.RecordRemoved(record);

        Assert.Empty(ledger.LoadInstalledByApp());
        var history = ledger.ReadHistory();
        Assert.Equal(2, history.Count);
        Assert.Contains("REMOVED PawnIO sensor driver", history[1], StringComparison.Ordinal);
        Assert.Contains("to get it back: winget install namazso.PawnIO", history[1], StringComparison.Ordinal);
    }

    [Fact]
    public void History_lines_older_than_15_days_are_pruned_but_newer_ones_kept()
    {
        var ledger = NewLedger();
        ledger.RecordInstalled(PawnIo(), now: DateTimeOffset.Now.AddDays(-20));
        ledger.RecordRemoved(PawnIo(), now: DateTimeOffset.Now.AddDays(-14));

        var history = ledger.ReadHistory(DateTimeOffset.Now);

        var line = Assert.Single(history);
        Assert.Contains("REMOVED", line, StringComparison.Ordinal); // 14 days old: kept
    }

    [Fact]
    public void Unparseable_history_lines_are_never_deleted()
    {
        var ledger = NewLedger();
        File.WriteAllLines(ledger.HistoryPath, ["a stray note the user typed themselves"]);
        ledger.PruneHistory(DateTimeOffset.Now.AddYears(1));

        Assert.Contains("a stray note", File.ReadAllText(ledger.HistoryPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Forgetting_without_history_removes_state_only()
    {
        var ledger = NewLedger();
        ledger.RecordInstalled(PawnIo());
        ledger.ForgetWithoutHistory("pawnio");

        Assert.Empty(ledger.LoadInstalledByApp());
        var line = Assert.Single(ledger.ReadHistory());
        Assert.Contains("INSTALLED", line, StringComparison.Ordinal); // no REMOVED line was invented
    }

    [Fact]
    public void Corrupt_state_file_reads_as_nothing_installed_by_the_app()
    {
        var ledger = NewLedger();
        ledger.RecordInstalled(PawnIo());
        File.WriteAllText(Path.Combine(_dir, "dependencies.json"), "{ not json !!");

        // Fail-safe direction: on corruption the app claims LESS, never more —
        // so the exit cleanup offers nothing rather than offering to remove a guess.
        Assert.Empty(NewLedger().LoadInstalledByApp());
    }
}

public class ConsumerCheckTests
{
    [Fact]
    public void Finds_running_consumers_case_insensitively()
    {
        var running = new[] { "explorer", "fancontrol", "chrome", "OPENRGB" };
        var found = ConsumerCheck.FindRunning(["FanControl", "OpenRGB", "LibreHardwareMonitor"], running);
        Assert.Equal(["FanControl", "OpenRGB"], found);
    }

    [Fact]
    public void Reports_nothing_when_no_consumer_is_running()
    {
        Assert.Empty(ConsumerCheck.FindRunning(["FanControl"], ["explorer", "chrome"]));
    }
}
