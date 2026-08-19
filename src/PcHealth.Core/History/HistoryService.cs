using Microsoft.Data.Sqlite;
using PcHealth.Core.Metrics;

namespace PcHealth.Core.History;

public sealed record HistoryOutcome(
    IReadOnlyList<TrendFinding> Trends,
    HistoryCoverage Coverage,
    int MeasurementsRecorded,
    string? Error)
{
    public static HistoryOutcome Disabled { get; } =
        new([], new HistoryCoverage(0, null, null), 0, null);
}

/// <summary>
/// Ties the two halves together in the right order: compare this scan against what is
/// already stored, and only then add this scan. Doing it the other way round would make
/// every counter look stable, because the newest sample would be the current one.
/// </summary>
public static class HistoryService
{
    public static HistoryOutcome AnalyzeAndRecord(
        IEnumerable<Metric> metrics,
        DateTimeOffset takenAt,
        string? databasePath = null)
    {
        var metricList = metrics.ToList();
        try
        {
            using var store = new HistoryStore(databasePath);
            var trends = TrendAnalyzer.Analyze(metricList, store);
            var recorded = store.RecordScan(takenAt, metricList);
            return new HistoryOutcome(trends, store.GetCoverage(), recorded, null);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Trend history is a convenience: never let it break a scan.
            return new HistoryOutcome([], new HistoryCoverage(0, null, null), 0,
                $"Trend history unavailable: {e.Message}");
        }
    }

    /// <summary>Deletes all stored history. Returns the path that was cleared, or an error.</summary>
    public static (bool Success, string Message) Clear(string? databasePath = null)
    {
        try
        {
            using var store = new HistoryStore(databasePath);
            store.Clear();
            return (true, $"Trend history deleted ({store.DatabasePath}).");
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return (false, $"Could not delete trend history: {e.Message}");
        }
    }
}
