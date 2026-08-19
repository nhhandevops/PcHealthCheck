using System.Globalization;
using Microsoft.Data.Sqlite;
using PcHealth.Core.Metrics;

namespace PcHealth.Core.History;

public sealed record Sample(DateTimeOffset TakenAt, double Value);

public sealed record HistoryCoverage(int ScanCount, DateTimeOffset? Oldest, DateTimeOffset? Newest)
{
    public double DaysCovered => Oldest is { } oldest && Newest is { } newest ? (newest - oldest).TotalDays : 0;

    /// <summary>Scans available for comparison, i.e. excluding the one just recorded.</summary>
    public int EarlierScans => Math.Max(0, ScanCount - 1);

    /// <summary>Plain-language span, e.g. "over 34 days" or "recorded today".</summary>
    public string SpanText => DaysCovered >= 1 ? $"over {DaysCovered:0} days" : "recorded today";
}

/// <summary>
/// A local SQLite record of the numbers each scan measured, so the app can tell the
/// difference between "3 bad sectors, same as last month" and "3 bad sectors, there
/// were none last week" — which is the difference between a note and an emergency.
///
/// PRIVACY: stores only metric ids, subjects (e.g. a drive model) and numbers, in the
/// app's own folder under %LOCALAPPDATA%. It never leaves the machine, and the user can
/// delete it at any time from the app or by deleting the file.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public string DatabasePath { get; }

    public HistoryStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcHealthCheck", "history.db");

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS scans (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                taken_at TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS samples (
                scan_id   INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
                metric_id TEXT    NOT NULL,
                subject   TEXT    NOT NULL DEFAULT '',
                value     REAL    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_samples_metric ON samples(metric_id, subject);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Stores every numeric measurement from one scan. Returns how many were kept.</summary>
    public int RecordScan(DateTimeOffset takenAt, IEnumerable<Metric> metrics)
    {
        using var transaction = _connection.BeginTransaction();

        long scanId;
        using (var insertScan = _connection.CreateCommand())
        {
            insertScan.Transaction = transaction;
            insertScan.CommandText = "INSERT INTO scans (taken_at) VALUES ($takenAt); SELECT last_insert_rowid();";
            insertScan.Parameters.AddWithValue("$takenAt", takenAt.ToString("o", CultureInfo.InvariantCulture));
            scanId = (long)(insertScan.ExecuteScalar() ?? 0L);
        }

        var kept = 0;
        using (var insertSample = _connection.CreateCommand())
        {
            insertSample.Transaction = transaction;
            insertSample.CommandText =
                "INSERT INTO samples (scan_id, metric_id, subject, value) VALUES ($scanId, $metricId, $subject, $value)";
            var scanParameter = insertSample.Parameters.Add("$scanId", SqliteType.Integer);
            var metricParameter = insertSample.Parameters.Add("$metricId", SqliteType.Text);
            var subjectParameter = insertSample.Parameters.Add("$subject", SqliteType.Text);
            var valueParameter = insertSample.Parameters.Add("$value", SqliteType.Real);
            scanParameter.Value = scanId;

            foreach (var metric in metrics)
            {
                if (metric.Value.Number is not { } number) continue; // only numbers form a trend
                metricParameter.Value = metric.Id;
                subjectParameter.Value = metric.Subject ?? "";
                valueParameter.Value = number;
                insertSample.ExecuteNonQuery();
                kept++;
            }
        }

        transaction.Commit();
        return kept;
    }

    /// <summary>All recorded values for one metric/subject, oldest first.</summary>
    public IReadOnlyList<Sample> GetSeries(string metricId, string? subject)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT scans.taken_at, samples.value
            FROM samples JOIN scans ON scans.id = samples.scan_id
            WHERE samples.metric_id = $metricId AND samples.subject = $subject
            ORDER BY scans.taken_at ASC
            """;
        command.Parameters.AddWithValue("$metricId", metricId);
        command.Parameters.AddWithValue("$subject", subject ?? "");

        var samples = new List<Sample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var takenAt))
            {
                samples.Add(new Sample(takenAt, reader.GetDouble(1)));
            }
        }
        return samples;
    }

    public HistoryCoverage GetCoverage()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MIN(taken_at), MAX(taken_at) FROM scans";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new HistoryCoverage(0, null, null);

        var count = reader.GetInt32(0);
        DateTimeOffset? oldest = reader.IsDBNull(1) ? null : ParseOrNull(reader.GetString(1));
        DateTimeOffset? newest = reader.IsDBNull(2) ? null : ParseOrNull(reader.GetString(2));
        return new HistoryCoverage(count, oldest, newest);
    }

    /// <summary>Deletes every stored measurement.</summary>
    public void Clear()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM samples; DELETE FROM scans; VACUUM;";
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset? ParseOrNull(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    public void Dispose() => _connection.Dispose();
}
