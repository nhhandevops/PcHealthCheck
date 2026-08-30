using PcHealth.Core.Modules;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Ties "a check could not finish" to "the module says so", because keeping those two facts in
/// separate variables is how this codebase has now shipped the same bug six times: a query fails,
/// a note is dutifully added, the module returns <see cref="ModuleStatus.Completed"/> anyway, and
/// the report's "some checks could not finish" banner never fires. The reader is told the PC is
/// fine by an app that did not look.
///
/// The counter-and-note pattern this replaces was correct in the modules that had it
/// (VolumesModule, LicenseModule, MalwareDefenderModule) and simply absent in the ones that did
/// not. Nothing about it was hard — it was just one more thing to remember per query, and
/// sixteen modules did not. Recording the note and incrementing the count are the same call here,
/// so they cannot come apart.
///
/// The distinction that matters, and the reason <see cref="Note"/> exists separately: *"I
/// deliberately did not check this"* — TRIM needing admin, a benchmark tool the user chose not to
/// install — must NOT degrade the status, or the banner fires on every healthy scan and stops
/// meaning anything. Only *"I tried and it broke"* counts as a failure.
/// </summary>
public sealed class ModuleFailures(List<string> notes)
{
    /// <summary>How many checks inside this module tried and could not finish.</summary>
    public int Count { get; private set; }

    public bool Any => Count > 0;

    /// <summary>
    /// A note that is NOT a failure: something deliberately not checked here, or a plain
    /// observation. Does not affect the module's status.
    /// </summary>
    public void Note(string message) => notes.Add(message);

    /// <summary>
    /// Records that <paramref name="what"/> could not be read, and degrades the module.
    /// </summary>
    public void Record(string what, string error)
    {
        notes.Add($"{what}: {error}");
        Count++;
    }

    /// <summary>
    /// Records a failure only when <paramref name="error"/> is non-null. Returns true when it
    /// recorded one, so a caller can skip work that depended on the failed read:
    /// <c>if (failures.RecordIf("Battery query", err)) return failures.Resolve();</c>
    /// </summary>
    public bool RecordIf(string what, string? error)
    {
        if (error is null) return false;
        Record(what, error);
        return true;
    }

    /// <summary>
    /// The status this module should return. <paramref name="whenClean"/> is what it would have
    /// returned had nothing failed — usually <see cref="ModuleStatus.Completed"/>, but a module
    /// that legitimately had nothing to do passes <see cref="ModuleStatus.Skipped"/>.
    ///
    /// A module that gathered nothing at all AND had failures is <see cref="ModuleStatus.Failed"/>
    /// rather than partial: there is no partial result to keep.
    /// </summary>
    public ModuleStatus Resolve(ModuleStatus whenClean = ModuleStatus.Completed, bool gotAnything = true)
    {
        if (Count == 0) return whenClean;
        return gotAnything ? ModuleStatus.PartiallyCompleted : ModuleStatus.Failed;
    }
}
