using System.Globalization;
using System.Management;
using PcHealth.Core.Modules;

namespace PcHealth.Modules.Windows.Support;

/// <summary>Thin WMI/CIM query helper. Read-only WQL SELECTs only.</summary>
internal static class Wmi
{
    /// <summary>
    /// Runs a WQL SELECT against the given namespace and returns each instance as a
    /// property dictionary. Throws on access/namespace errors — callers decide whether
    /// that means Failed, PartiallyCompleted, or a note.
    /// </summary>
    public static List<Dictionary<string, object?>> Query(string scope, string wql)
    {
        var results = new List<Dictionary<string, object?>>();
        var options = new System.Management.EnumerationOptions { Rewindable = false, ReturnImmediately = true };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        using var collection = searcher.Get();
        foreach (ManagementBaseObject item in collection)
        {
            using (item)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.Properties)
                {
                    row[prop.Name] = prop.Value;
                }
                results.Add(row);
            }
        }
        return results;
    }

    /// <summary>
    /// Query that records its own failure. Preferred over the <c>out string?</c> overload,
    /// because that one hands the caller an error it is free to note and then ignore — which is
    /// exactly how a module ends up reporting <c>Completed</c> about a check that never ran.
    /// Here the failure reaches the module's status whether the caller thinks about it or not.
    ///
    /// <paramref name="what"/> names the thing that could not be read, in words the person
    /// reading the report would use: "Battery query", not "Win32_Battery".
    /// </summary>
    public static List<Dictionary<string, object?>> TryQuery(
        string scope, string wql, ModuleFailures failures, string what)
    {
        var rows = TryQuery(scope, wql, out var error);
        failures.RecordIf(what, error);
        return rows;
    }

    /// <summary>Query that swallows failures into an error string instead of throwing.</summary>
    public static List<Dictionary<string, object?>> TryQuery(string scope, string wql, out string? error)
    {
        try
        {
            error = null;
            return Query(scope, wql);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return [];
        }
    }

    /// <summary>
    /// Converts a WMI property to <typeparamref name="T"/>. Always converts through the
    /// invariant culture: WMI sometimes hands back numbers as strings, and on a locale
    /// that uses "," as the decimal separator a current-culture conversion silently
    /// misreads them (or throws).
    /// </summary>
    public static T? Get<T>(Dictionary<string, object?> row, string key) where T : struct
    {
        if (row.TryGetValue(key, out var value) && value is not null)
        {
            try { return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { }
        }
        return null;
    }

    public static string? GetString(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value)
            ? value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value?.ToString()
            : null;

    public static DateTime? GetCimDate(Dictionary<string, object?> row, string key)
    {
        var raw = GetString(row, key);
        if (string.IsNullOrEmpty(raw)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
