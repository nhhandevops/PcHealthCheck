using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHealth.Core.Reporting;

/// <summary>Machine-readable export. Written only to a user-chosen local path.</summary>
public static class JsonReportExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(ScanReport report) => JsonSerializer.Serialize(report, Options);

    public static void WriteTo(ScanReport report, string path) => File.WriteAllText(path, ToJson(report));
}
