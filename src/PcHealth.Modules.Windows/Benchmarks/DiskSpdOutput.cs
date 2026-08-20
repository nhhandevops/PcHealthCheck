using System.Globalization;
using System.Xml.Linq;

namespace PcHealth.Modules.Windows.Benchmarks;

/// <summary>One measured pass: how fast the drive fed data back, and how long it waited.</summary>
public sealed record DiskSpdReading(
    double ReadMegabytesPerSecond,
    double ReadIops,
    double AverageLatencyMs,
    double TestSeconds);

/// <summary>
/// Reads DiskSpd's <c>-Rxml</c> results.
///
/// XML rather than DiskSpd's text output for the usual reason: the text is formatted for
/// people and the numbers move around in it, while the XML element names are part of the
/// tool's contract. Totals are summed across every thread and target because DiskSpd
/// reports per-thread, and a multi-threaded run's headline figure is the sum.
///
/// Speed is reported in decimal MB/s (1 MB = 1,000,000 bytes), which is what CrystalDiskMark
/// and drive manufacturers quote — so the number here is comparable to the one on the box.
/// </summary>
public static class DiskSpdOutputReader
{
    public static DiskSpdReading? Parse(string xml, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            error = "DiskSpd produced no output.";
            return null;
        }

        XElement? timeSpan;
        try
        {
            timeSpan = XDocument.Parse(xml).Root?.Element("TimeSpan");
        }
        catch (System.Xml.XmlException e)
        {
            error = $"DiskSpd's results could not be read: {e.Message}";
            return null;
        }

        if (timeSpan is null)
        {
            error = "DiskSpd's results contained no measurement.";
            return null;
        }

        // DiskSpd says so itself when a run was cut short — believe it rather than
        // dividing whatever partial counters happen to be present.
        if (timeSpan.Element("Error") is { } failure)
        {
            error = failure.Value.Trim();
            return null;
        }

        var seconds = Number(timeSpan.Element("TestTimeSeconds"));
        if (seconds is not > 0)
        {
            error = "DiskSpd reported a test duration of zero, so no speed can be calculated.";
            return null;
        }

        double bytes = 0, ios = 0, latencyWeighted = 0, latencyWeight = 0;
        foreach (var target in timeSpan.Elements("Thread").Elements("Target"))
        {
            var targetBytes = Number(target.Element("ReadBytes")) ?? 0;
            var targetIos = Number(target.Element("ReadCount")) ?? 0;
            bytes += targetBytes;
            ios += targetIos;

            // Averaging the per-thread averages unweighted would let a thread that did
            // almost no work pull the figure around.
            if (Number(target.Element("Latency")?.Element("AverageReadLatencyMilliseconds")) is { } latency && targetIos > 0)
            {
                latencyWeighted += latency * targetIos;
                latencyWeight += targetIos;
            }
        }

        if (ios <= 0)
        {
            error = "DiskSpd completed but recorded no reads.";
            return null;
        }

        return new DiskSpdReading(
            ReadMegabytesPerSecond: bytes / seconds.Value / 1_000_000,
            ReadIops: ios / seconds.Value,
            AverageLatencyMs: latencyWeight > 0 ? latencyWeighted / latencyWeight : 0,
            TestSeconds: seconds.Value);
    }

    /// <summary>DiskSpd writes plain invariant numbers; parsing them under a comma-decimal
    /// locale would silently turn 1234.56 into 123456.</summary>
    private static double? Number(XElement? element) =>
        element is not null && double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
