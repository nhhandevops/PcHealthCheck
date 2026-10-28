namespace PcHealth.Core.Metrics;

/// <summary>
/// The readings that say a drive's storage is damaged (STEP-B-SPEC 2.5). Two checks ask the same
/// question of them: the disk benchmark, which never writes to a drive one of them grades Normal or
/// Bad (REMEDIES-PLAN H2), and the "What you can do" advice, which tells the owner to back that
/// drive up (H1). Kept in one place so the two always agree on what a failing drive is.
/// </summary>
/// <remarks>
/// Each one's Good band is "none at all" (or, for the drive's own failure flag, "not predicting
/// failure"), so a counter that grew since an earlier scan is already graded Normal or Bad, and the
/// history need not be read separately. Cable (CRC) errors, command timeouts and temperature are
/// not here: they say nothing about whether the storage itself is damaged.
/// </remarks>
public static class DriveDamage
{
    public static readonly IReadOnlySet<string> MetricIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "smart.predict_failure", "smart.attributes_at_threshold", "smart.reallocated", "smart.pending_sectors",
        "smart.offline_uncorrectable", "smart.reported_uncorrectable",
        "storage.read_errors_uncorrected", "storage.write_errors_uncorrected",
    };
}
