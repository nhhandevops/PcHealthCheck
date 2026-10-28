using Microsoft.Win32;

namespace PcHealth.Modules.Windows.Startup;

/// <summary>
/// Task Manager's "disabled" switches for startup items, from
/// <c>Explorer\StartupApproved</c> in each hive.
///
/// A switch belongs to one place, not to a name. Each hive keeps one subkey per location:
/// <c>Run</c> for that hive's Run key, <c>Run32</c> for HKLM's 32-bit Run key, and
/// <c>StartupFolder</c> for that hive's Startup folder, by file name with its extension. The old
/// code merged all six into one map by value name, so a switch filed for one place could decide
/// an item of the same name in another, and it never applied the StartupFolder switches at all:
/// a shortcut turned off in Task Manager was still listed and graded as starting (START-001).
///
/// The value's first byte is odd when the item is disabled (03, and 01 or 05 seen on the dev
/// T470p with a disable time after it) and even when enabled (02, 04 or 06, time zeroed).
/// </summary>
public sealed class StartupApproved
{
    public const string Run = "Run";
    public const string Run32 = "Run32";
    public const string StartupFolder = "StartupFolder";

    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="values">Each switch as (hive, location, name, value), hive being "HKLM" or "HKCU".</param>
    public StartupApproved(IEnumerable<(string Hive, string Location, string Name, byte[] Value)> values)
    {
        foreach (var (hive, location, name, value) in values)
        {
            if (MeansDisabled(value)) _disabled.Add(Key(hive, location, name));
        }
    }

    public static bool MeansDisabled(byte[] value) => value.Length > 0 && (value[0] & 0x01) == 0x01;

    public bool IsDisabled(string hive, string location, string name) => _disabled.Contains(Key(hive, location, name));

    public static StartupApproved Read()
    {
        var values = new List<(string, string, string, byte[])>();
        foreach (var (hive, hiveName) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            foreach (var location in new[] { Run, Run32, StartupFolder })
            {
                using var key = hive.OpenSubKey($@"{KeyPath}\{location}");
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is byte[] bytes) values.Add((hiveName, location, name, bytes));
                }
            }
        }
        return new StartupApproved(values);
    }

    private static string Key(string hive, string location, string name) => $@"{hive}\{location}\{name}";
}
