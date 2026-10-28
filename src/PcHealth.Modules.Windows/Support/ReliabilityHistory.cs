using PcHealth.Core.Modules;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// What an empty Reliability Monitor history means. On an older install it means Windows is not
/// collecting reliability data, and the scan says it could not finish. On a young install it is
/// simply too early: a Note, not a failure, so a freshly installed PC does not raise the "some
/// checks could not finish" banner (REMEDIES-PLAN Step A row 10).
/// </summary>
public static class ReliabilityHistory
{
    public const string DisabledNote = "Reliability data collection (RAC scheduled task) appears to be disabled on this system.";

    /// <param name="youngInstallDays">Days since install, only when Windows is young (<see cref="InstallAge.Young"/>).</param>
    public static (ModuleStatus Status, string Note) WhenEmpty(int? youngInstallDays) => youngInstallDays is { } days
        ? (ModuleStatus.Skipped, InstallAge.NotJudged("Reliability history", days, "On a new install, Windows may not have built that history yet."))
        : (ModuleStatus.PartiallyCompleted, DisabledNote);
}
