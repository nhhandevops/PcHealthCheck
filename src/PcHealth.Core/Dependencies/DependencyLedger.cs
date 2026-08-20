using System.Globalization;
using System.Text.Json;

namespace PcHealth.Core.Dependencies;

/// <summary>One tool this app itself installed (never anything that was already present).</summary>
public sealed record InstalledDependency(
    string Id,
    string Name,
    DateTimeOffset InstalledAt,
    string InstallCommand,
    string ReinstallHint);

/// <summary>
/// Remembers exactly what THIS app installed, and keeps a human-readable removal log.
///
/// Two files under %LOCALAPPDATA%\PcHealthCheck:
///  - dependencies.json — the machine state: only tools this app installed. A tool that
///    was already on the PC never enters this file, which is what guarantees the exit
///    cleanup can never offer to remove something the user installed themselves.
///  - dependency-history.txt — a plain-text log of installs/removals with the reinstall
///    command, kept for 15 days so the user can get a removed tool back easily.
/// </summary>
public sealed class DependencyLedger
{
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(15);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _statePath;

    public string HistoryPath { get; }

    public DependencyLedger(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHealthCheck");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "dependencies.json");
        HistoryPath = Path.Combine(dir, "dependency-history.txt");
    }

    /// <summary>Everything this app installed and has not yet removed. Empty on corruption.</summary>
    public Dictionary<string, InstalledDependency> LoadInstalledByApp()
    {
        try
        {
            if (!File.Exists(_statePath)) return [];
            using var stream = File.OpenRead(_statePath);
            return JsonSerializer.Deserialize<Dictionary<string, InstalledDependency>>(stream, JsonOptions) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void RecordInstalled(InstalledDependency dependency, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.Now;
        var state = LoadInstalledByApp();
        state[dependency.Id] = dependency;
        Save(state);
        AppendHistory(stamp,
            $"INSTALLED {dependency.Name} | by PcHealthCheck | command: {dependency.InstallCommand}");
    }

    public void RecordRemoved(InstalledDependency dependency, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.Now;
        var state = LoadInstalledByApp();
        state.Remove(dependency.Id);
        Save(state);
        AppendHistory(stamp,
            $"REMOVED {dependency.Name} | had been installed by this app on {dependency.InstalledAt:yyyy-MM-dd} | " +
            $"to get it back: {dependency.ReinstallHint}");
    }

    /// <summary>State cleanup when the user removed the tool themselves, outside this app.</summary>
    public void ForgetWithoutHistory(string id)
    {
        var state = LoadInstalledByApp();
        if (state.Remove(id)) Save(state);
    }

    public IReadOnlyList<string> ReadHistory(DateTimeOffset? now = null)
    {
        PruneHistory(now ?? DateTimeOffset.Now);
        return File.Exists(HistoryPath) ? File.ReadAllLines(HistoryPath) : [];
    }

    /// <summary>Drops log lines older than the 15-day retention. Unparseable lines are kept.</summary>
    public void PruneHistory(DateTimeOffset now)
    {
        if (!File.Exists(HistoryPath)) return;
        var cutoff = now - HistoryRetention;
        var kept = File.ReadAllLines(HistoryPath)
            .Where(line => !TryParseTimestamp(line, out var stamp) || stamp >= cutoff)
            .ToArray();
        File.WriteAllLines(HistoryPath, kept);
    }

    private void AppendHistory(DateTimeOffset now, string text)
    {
        PruneHistory(now);
        File.AppendAllText(HistoryPath,
            $"[{now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}] {text}{Environment.NewLine}");
    }

    private void Save(Dictionary<string, InstalledDependency> state) =>
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions));

    private static bool TryParseTimestamp(string line, out DateTimeOffset stamp)
    {
        stamp = default;
        if (line.Length < 18 || line[0] != '[') return false;
        var close = line.IndexOf(']', StringComparison.Ordinal);
        if (close != 17) return false;
        if (!DateTime.TryParseExact(line[1..close], "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return false;
        }
        stamp = new DateTimeOffset(parsed);
        return true;
    }
}

/// <summary>Pure logic for the "is anything else using this tool right now" check.</summary>
public static class ConsumerCheck
{
    public static IReadOnlyList<string> FindRunning(
        IEnumerable<string> knownConsumers, IEnumerable<string> runningProcessNames)
    {
        var running = new HashSet<string>(runningProcessNames, StringComparer.OrdinalIgnoreCase);
        return [.. knownConsumers.Where(running.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Order()];
    }
}
