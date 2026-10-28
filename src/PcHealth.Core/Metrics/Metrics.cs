using System.Globalization;

namespace PcHealth.Core.Metrics;

public enum Grade
{
    Good,
    Normal,
    Bad,

    /// <summary>Metric could not be measured or has no rule — shown neutrally, never counted against the score.</summary>
    Unknown,
}

public enum HealthCategory
{
    Storage,
    Performance,
    Sensors,
    Security,
    SoftwareOs,
    Stability,
    Battery,

    /// <summary>Added 2026-10-21, last so stored values of the others keep their numbers.</summary>
    Memory,
}

/// <summary>A typed metric value: exactly one of Number / Text / Flag is set.</summary>
public readonly record struct MetricValue
{
    public double? Number { get; init; }
    public string? Text { get; init; }
    public bool? Flag { get; init; }

    public static implicit operator MetricValue(double v) => new() { Number = v };
    public static implicit operator MetricValue(bool v) => new() { Flag = v };
    public static implicit operator MetricValue(string v) => new() { Text = v };

    public override string ToString() =>
        Number is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture)
        : Text ?? (Flag is { } f ? (f ? "yes" : "no") : "n/a");
}

/// <summary>
/// One measured fact, e.g. ("storage.volume.free_pct", 12.4, Subject "C:").
/// <see cref="Id"/> is the key the verdict rules select on; the same Id may occur
/// once per <see cref="Subject"/> (per volume, per disk, per AV product...).
/// <see cref="DiskNumber"/> is Windows' number for the physical drive a drive-health reading
/// came from (the N in \\.\PhysicalDriveN), so another check can find the same drive by its
/// number. The subject is a model name, and two identical drives share one.
/// </summary>
public sealed record Metric(
    string Id,
    string Label,
    MetricValue Value,
    HealthCategory Category,
    string? Unit = null,
    string? Detail = null,
    string? Subject = null,
    bool InfoOnly = false,
    int? DiskNumber = null);
