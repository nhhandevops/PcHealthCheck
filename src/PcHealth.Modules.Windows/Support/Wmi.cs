using System.Management;

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

    public static T? Get<T>(Dictionary<string, object?> row, string key) where T : struct
    {
        if (row.TryGetValue(key, out var value) && value is not null)
        {
            try { return (T)Convert.ChangeType(value, typeof(T)); }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { }
        }
        return null;
    }

    public static string? GetString(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    public static DateTime? GetCimDate(Dictionary<string, object?> row, string key)
    {
        var raw = GetString(row, key);
        if (string.IsNullOrEmpty(raw)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
