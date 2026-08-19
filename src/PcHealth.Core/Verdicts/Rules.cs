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

    /// <summary>Loads every rules/*.json embedded in PcHealth.Core.</summary>
    public static IReadOnlyList<Rule> LoadEmbedded()
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
