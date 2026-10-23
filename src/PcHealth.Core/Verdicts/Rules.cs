using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHealth.Core.Verdicts;

/// <summary>One rules file (rules/*.json embedded in this assembly).</summary>
public sealed class RuleSet
{
    [JsonPropertyName("rules")]
    public List<Rule> Rules { get; set; } = [];
}

/// <summary>
/// Grades one metric id. Bands are evaluated in order; the first matching band wins.
/// No matching band (or no rule at all) yields Grade.Unknown.
/// </summary>
public sealed class Rule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("metricId")]
    public string MetricId { get; set; } = "";

    /// <summary>
    /// The one OS this rule applies on ("windows", "linux"), or null for every OS. A metric such
    /// as free space means the same everywhere, but its tips do not: "Storage Sense" on a Linux
    /// report is advice the reader cannot follow. Such metrics get one rule per OS, and
    /// <see cref="RulesLoader.LoadEmbedded(string)"/> keeps only the current OS's.
    /// </summary>
    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("bands")]
    public List<Band> Bands { get; set; } = [];

    /// <summary>Fix tips keyed by grade name ("Normal", "Bad").</summary>
    [JsonPropertyName("tips")]
    public Dictionary<string, List<string>>? Tips { get; set; }

    /// <summary>Forecast / prevention note attached to any non-Good verdict.</summary>
    [JsonPropertyName("forecast")]
    public string? Forecast { get; set; }
}

/// <summary>
/// A single band: all present numeric constraints must hold (AND). "eq"/"anyOf"
/// compare against text, flag, or number depending on the metric's value kind.
/// "always": true is the catch-all final band.
/// </summary>
public sealed class Band
{
    [JsonPropertyName("grade")]
    public string Grade { get; set; } = "Unknown";

    [JsonPropertyName("lt")] public double? Lt { get; set; }
    [JsonPropertyName("lte")] public double? Lte { get; set; }
    [JsonPropertyName("gt")] public double? Gt { get; set; }
    [JsonPropertyName("gte")] public double? Gte { get; set; }
    [JsonPropertyName("eq")] public JsonElement? Eq { get; set; }
    [JsonPropertyName("anyOf")] public List<JsonElement>? AnyOf { get; set; }
    [JsonPropertyName("always")] public bool Always { get; set; }
}

public static class RulesLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The OS names rules use in <see cref="Rule.Os"/>.</summary>
    public static readonly IReadOnlyList<string> KnownOs = ["windows", "linux"];

    /// <summary>The <see cref="Rule.Os"/> name of the OS this process runs on.</summary>
    public static string CurrentOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "other";

    /// <summary>Loads the embedded rules that apply on the OS this process runs on.</summary>
    public static IReadOnlyList<Rule> LoadEmbedded() => LoadEmbedded(CurrentOs);

    /// <summary>
    /// Loads every rules/*.json embedded in PcHealth.Core, keeping the rules for
    /// <paramref name="os"/> and those for every OS.
    /// </summary>
    public static IReadOnlyList<Rule> LoadEmbedded(string os) =>
        [.. LoadAllEmbedded().Where(r => r.Os is null || string.Equals(r.Os, os, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Every embedded rule, for every OS. Only for tests that check the files themselves.</summary>
    public static IReadOnlyList<Rule> LoadAllEmbedded()
    {
        var assembly = typeof(RulesLoader).Assembly;
        var rules = new List<Rule>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Rules.", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource vanished: {name}");
            var set = JsonSerializer.Deserialize<RuleSet>(stream, Options)
                ?? throw new InvalidOperationException($"Rules file {name} deserialized to null.");
            rules.AddRange(set.Rules);
        }
        return rules;
    }
}
