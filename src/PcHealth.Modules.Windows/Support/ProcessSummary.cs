using System.Globalization;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// The programs using the most memory and processor time, by name only. Without it the T470p's
/// report said memory was failing but not who held it (Brave, 29 processes, 5.5 GB; VS Code;
/// dwm.exe leaking under an old graphics driver), and the reader had to open Task Manager to
/// act on the finding (feature gap 5).
///
/// Instances of one program are summed, because a browser runs as dozens of processes and the
/// one number that matters is the total. Names only: no paths, window titles or command lines.
/// </summary>
public static class ProcessSummary
{
    public const int Shown = 5;

    public static string TopByMemory(IEnumerable<(string Name, long PrivateBytes)> processes) =>
        Format(processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.First().Name, Instances: g.Count(), Value: (double)g.Sum(p => p.PrivateBytes)))
                .Where(g => g.Value > 0),
            bytes => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:0.0} GB"));

    public static string TopByCpu(IEnumerable<(string Name, TimeSpan Cpu)> processes) =>
        Format(processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.First().Name, Instances: g.Count(), Value: g.Sum(p => p.Cpu.TotalSeconds)))
                .Where(g => g.Value >= 1),
            seconds => seconds >= 3600
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.0} h")
                : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(seconds / 60))} min"));

    private static string Format(IEnumerable<(string Name, int Instances, double Value)> totals, Func<double, string> unit) =>
        string.Join("; ", totals
            .OrderByDescending(t => t.Value)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Shown)
            .Select(t => t.Instances > 1
                ? string.Create(CultureInfo.InvariantCulture, $"{t.Name} ×{t.Instances} {unit(t.Value)}")
                : $"{t.Name} {unit(t.Value)}"));
}
