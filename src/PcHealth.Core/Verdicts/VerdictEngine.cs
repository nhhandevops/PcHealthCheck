using System.Text.Json;
using PcHealth.Core.Metrics;

namespace PcHealth.Core.Verdicts;

public sealed record Verdict(
    Metric Metric,
    Grade Grade,
    string? RuleId,
    IReadOnlyList<string> Tips,
    string? Forecast);

/// <summary>
/// Pure, data-driven grading: metric id -> rule -> first matching band -> grade + tips.
/// Metrics marked InfoOnly, and metrics with no rule or no matching band, come back
/// as Grade.Unknown and are never counted against the score.
/// </summary>
public sealed class VerdictEngine
{
    private readonly Dictionary<string, Rule> _rulesByMetricId;

    public VerdictEngine(IEnumerable<Rule> rules)
    {
        _rulesByMetricId = new Dictionary<string, Rule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!_rulesByMetricId.TryAdd(rule.MetricId, rule))
                throw new InvalidOperationException($"Duplicate rule for metric '{rule.MetricId}' ({rule.Id}).");
        }
    }

    public IReadOnlyList<Verdict> Evaluate(IEnumerable<Metric> metrics)
    {
        var verdicts = new List<Verdict>();
        foreach (var metric in metrics)
        {
            verdicts.Add(EvaluateOne(metric));
        }
        return verdicts;
    }

    public Verdict EvaluateOne(Metric metric)
    {
        if (metric.InfoOnly || !_rulesByMetricId.TryGetValue(metric.Id, out var rule))
            return new Verdict(metric, Grade.Unknown, null, [], null);

        foreach (var band in rule.Bands)
        {
            if (!Matches(band, metric.Value)) continue;

            var grade = Enum.TryParse<Grade>(band.Grade, ignoreCase: true, out var g) ? g : Grade.Unknown;
            var tips = grade is Grade.Normal or Grade.Bad && rule.Tips is not null
                       && rule.Tips.TryGetValue(grade.ToString(), out var t)
                ? (IReadOnlyList<string>)t
                : [];
            var forecast = grade is Grade.Normal or Grade.Bad ? rule.Forecast : null;
            return new Verdict(metric, grade, rule.Id, tips, forecast);
        }

        return new Verdict(metric, Grade.Unknown, rule.Id, [], null);
    }

    private static bool Matches(Band band, MetricValue value)
    {
        if (band.Always) return true;

        var hasNumericConstraint = band.Lt.HasValue || band.Lte.HasValue || band.Gt.HasValue || band.Gte.HasValue;
        if (hasNumericConstraint)
        {
            if (value.Number is not { } n) return false;
            if (band.Lt.HasValue && !(n < band.Lt.Value)) return false;
            if (band.Lte.HasValue && !(n <= band.Lte.Value)) return false;
            if (band.Gt.HasValue && !(n > band.Gt.Value)) return false;
            if (band.Gte.HasValue && !(n >= band.Gte.Value)) return false;
        }

        if (band.Eq is { } eq && !ValueEquals(eq, value)) return false;

        if (band.AnyOf is { Count: > 0 } anyOf && !anyOf.Any(candidate => ValueEquals(candidate, value))) return false;

        return hasNumericConstraint || band.Eq.HasValue || band.AnyOf is { Count: > 0 };
    }

    private static bool ValueEquals(JsonElement expected, MetricValue actual) => expected.ValueKind switch
    {
        JsonValueKind.Number => actual.Number is { } n && Math.Abs(n - expected.GetDouble()) < 1e-9,
        JsonValueKind.String => actual.Text is { } t && string.Equals(t, expected.GetString(), StringComparison.OrdinalIgnoreCase),
        JsonValueKind.True => actual.Flag == true,
        JsonValueKind.False => actual.Flag == false,
        _ => false,
    };
}
