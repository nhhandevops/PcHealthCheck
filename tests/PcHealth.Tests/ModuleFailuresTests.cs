using PcHealth.Core.Modules;
using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Locks the distinction this app has now got wrong six times: a check that TRIED AND BROKE must
/// degrade the module, and a check DELIBERATELY not run here must not.
///
/// Both directions are tested on purpose, and the second direction matters as much as the first.
/// Over-correcting — making every "I did not check this" degrade — would fire the report's
/// "Some checks could not finish" banner on every healthy scan of every ordinary PC, and a
/// warning that is always on teaches the reader to ignore it. That would be a worse bug than the
/// one this helper exists to prevent.
/// </summary>
public class ModuleFailuresTests
{
    [Fact]
    public void CleanRun_StaysCompleted()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);

        Assert.False(failures.Any);
        Assert.Equal(ModuleStatus.Completed, failures.Resolve());
        Assert.Empty(notes);
    }

    [Fact]
    public void DeliberateNote_DoesNotDegrade()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);

        failures.Note("TRIM status needs administrator rights and is checked in the elevated scan.");

        // The note reaches the reader; the status does not move. This is the whole point.
        Assert.Single(notes);
        Assert.False(failures.Any);
        Assert.Equal(ModuleStatus.Completed, failures.Resolve());
    }

    [Fact]
    public void RecordedFailure_DegradesAndIsSaidOutLoud()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);

        failures.Record("Battery query", "ManagementException: Invalid class");

        Assert.True(failures.Any);
        Assert.Equal(1, failures.Count);
        Assert.Equal(ModuleStatus.PartiallyCompleted, failures.Resolve());
        // A degraded status with no explanation is only marginally better than a false clean one.
        Assert.Contains(notes, n => n.Contains("Battery query", StringComparison.Ordinal)
                                    && n.Contains("Invalid class", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordIf_IgnoresSuccess_AndReportsWhetherItRecorded()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);

        Assert.False(failures.RecordIf("Defender state", null));
        Assert.Empty(notes);
        Assert.Equal(ModuleStatus.Completed, failures.Resolve());

        Assert.True(failures.RecordIf("Defender state", "COMException: 0x800706BA"));
        Assert.Equal(ModuleStatus.PartiallyCompleted, failures.Resolve());
    }

    [Fact]
    public void NothingGathered_IsFailedNotPartial()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        failures.Record("Disk inventory", "UnauthorizedAccessException");

        // There is no partial result to keep, so calling it "partially completed" overstates
        // what the module managed to do.
        Assert.Equal(ModuleStatus.Failed, failures.Resolve(gotAnything: false));
        Assert.Equal(ModuleStatus.PartiallyCompleted, failures.Resolve(gotAnything: true));
    }

    [Fact]
    public void SkippedModule_StaysSkippedWhenNothingBroke()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        failures.Note("No battery detected — desktop system, module skipped.");

        // A desktop with no battery is not an incomplete scan. If this ever returns
        // PartiallyCompleted, every desktop in the world raises the amber box.
        Assert.Equal(ModuleStatus.Skipped, failures.Resolve(ModuleStatus.Skipped));
    }

    [Fact]
    public void SkippedModule_StillDegradesWhenSomethingBroke()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);
        failures.Record("Battery query", "ManagementException");

        // "I meant to skip this" must not launder a query that actually failed.
        Assert.Equal(ModuleStatus.PartiallyCompleted, failures.Resolve(ModuleStatus.Skipped));
    }

    [Fact]
    public void FailuresAccumulate()
    {
        var notes = new List<string>();
        var failures = new ModuleFailures(notes);

        failures.Record("Restore points", "e1");
        failures.Note("File History service is not installed on this edition.");
        failures.Record("Shadow copies", "e2");

        Assert.Equal(2, failures.Count);
        Assert.Equal(3, notes.Count);
    }
}
