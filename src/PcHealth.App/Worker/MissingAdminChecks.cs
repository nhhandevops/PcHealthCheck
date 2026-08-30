using PcHealth.Core.Modules;

namespace PcHealth.App.Worker;

/// <summary>
/// Turns "an administrator check the user selected never reported anything" into a result that
/// says so.
///
/// The elevated half of a scan can end without producing a result for a module in several
/// ordinary ways: the UAC prompt was declined, the helper could not start or died mid-scan, or
/// the helper refused an id it did not recognise. Merging only the results that did arrive makes
/// those checks vanish — nothing downstream knows they were ever selected, so the "some checks
/// could not finish" banner has nothing to find and the scan reports GOOD with an entire check
/// missing. That was defect #5 of the 2026-08-22 acceptance run.
///
/// It lives in its own file because both entry points merge the same outcome and both have to
/// account for the same gap; the fix was made in the GUI path and the headless path kept the
/// original bug for another four days. Both callers now go through here, so there is one of this
/// left to forget rather than two.
/// </summary>
internal static class MissingAdminChecks
{
    /// <summary>
    /// One <see cref="ModuleStatus.Failed"/> result per selected admin module that is absent from
    /// <paramref name="gathered"/>. <paramref name="reason"/> is the elevated run's own message,
    /// which is the nearest thing to an explanation available; when there is none, all that is
    /// honestly known is that the check needed rights it never got.
    /// </summary>
    public static IReadOnlyList<ModuleResult> AsFailedResults(
        IEnumerable<ModuleResult> gathered,
        IEnumerable<IHealthModule> selectedAdminModules,
        string? reason)
    {
        var produced = gathered.Select(r => r.ModuleId).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. selectedAdminModules
                .Where(m => !produced.Contains(m.Manifest.Id))
                .Select(m => new ModuleResult
                {
                    ModuleId = m.Manifest.Id,
                    Status = ModuleStatus.Failed,
                    Error = reason ?? "This check needs administrator rights and did not run.",
                })
        ];
    }
}
