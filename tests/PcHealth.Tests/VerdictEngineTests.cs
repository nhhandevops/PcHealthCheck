using System.Text.Json;
using PcHealth.Core.Metrics;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

public class VerdictEngineTests
{
    private static Rule NumericRule(string metricId = "m.num") => Parse($$"""
        {
          "id": "r1", "metricId": "{{metricId}}",
          "bands": [
            { "grade": "Good", "lte": 35 },
            { "grade": "Normal", "lte": 60 },
            { "grade": "Bad", "always": true }
          ],
          "tips": { "Normal": ["do a thing"], "Bad": ["do it now"] },
          "forecast": "it will get worse"
        }
        """);

    private static Rule Parse(string json) =>
        JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static Metric Num(string id, double v) => new(id, id, v, HealthCategory.SoftwareOs);

    [Theory]
    [InlineData(0, Grade.Good)]
    [InlineData(35, Grade.Good)]
    [InlineData(36, Grade.Normal)]
    [InlineData(60, Grade.Normal)]
    [InlineData(61, Grade.Bad)]
    [InlineData(9999, Grade.Bad)]
    public void Numeric_bands_first_match_wins(double value, Grade expected)
    {
        var engine = new VerdictEngine([NumericRule()]);
        Assert.Equal(expected, engine.EvaluateOne(Num("m.num", value)).Grade);
    }

    [Fact]
    public void Tips_and_forecast_attach_only_to_non_good_grades()
    {
        var engine = new VerdictEngine([NumericRule()]);
        var good = engine.EvaluateOne(Num("m.num", 10));
        var normal = engine.EvaluateOne(Num("m.num", 50));
        var bad = engine.EvaluateOne(Num("m.num", 100));

        Assert.Empty(good.Tips);
        Assert.Null(good.Forecast);
        Assert.Equal(["do a thing"], normal.Tips);
        Assert.Equal(["do it now"], bad.Tips);
        Assert.Equal("it will get worse", bad.Forecast);
    }

    [Fact]
    public void Bool_eq_bands_grade_flags()
    {
        var rule = Parse("""
            { "id": "r2", "metricId": "m.flag",
              "bands": [ { "grade": "Good", "eq": true }, { "grade": "Bad", "eq": false } ] }
            """);
        var engine = new VerdictEngine([rule]);
        Assert.Equal(Grade.Good, engine.EvaluateOne(new Metric("m.flag", "f", true, HealthCategory.Security)).Grade);
        Assert.Equal(Grade.Bad, engine.EvaluateOne(new Metric("m.flag", "f", false, HealthCategory.Security)).Grade);
    }

    [Fact]
    public void AnyOf_matches_text_case_insensitively()
    {
        var rule = Parse("""
            { "id": "r3", "metricId": "m.text",
              "bands": [
                { "grade": "Good", "eq": "Genuine" },
                { "grade": "Bad", "anyOf": ["InvalidLicense", "Tampered"] },
                { "grade": "Normal", "always": true } ] }
            """);
        var engine = new VerdictEngine([rule]);
        Assert.Equal(Grade.Good, engine.EvaluateOne(new Metric("m.text", "t", "genuine", HealthCategory.SoftwareOs)).Grade);
        Assert.Equal(Grade.Bad, engine.EvaluateOne(new Metric("m.text", "t", "TAMPERED", HealthCategory.SoftwareOs)).Grade);
        Assert.Equal(Grade.Normal, engine.EvaluateOne(new Metric("m.text", "t", "somethingelse", HealthCategory.SoftwareOs)).Grade);
    }

    [Fact]
    public void Unruled_and_info_only_metrics_are_unknown()
    {
        var engine = new VerdictEngine([NumericRule()]);
        Assert.Equal(Grade.Unknown, engine.EvaluateOne(Num("m.other", 1)).Grade);
        Assert.Equal(Grade.Unknown, engine.EvaluateOne(
            new Metric("m.num", "n", 100, HealthCategory.SoftwareOs, InfoOnly: true)).Grade);
    }

    [Fact]
    public void Text_metric_never_matches_numeric_band()
    {
        var engine = new VerdictEngine([NumericRule()]);
        var verdict = engine.EvaluateOne(new Metric("m.num", "n", "not-a-number", HealthCategory.SoftwareOs));
        Assert.Equal(Grade.Bad, verdict.Grade); // falls through numeric bands to the "always" band
    }

    [Fact]
    public void Duplicate_metric_rules_throw()
    {
        Assert.Throws<InvalidOperationException>(() => new VerdictEngine([NumericRule(), NumericRule()]));
    }

    [Fact]
    public void Embedded_rules_load_parse_and_are_unique()
    {
        var rules = RulesLoader.LoadEmbedded();
        Assert.NotEmpty(rules);
        _ = new VerdictEngine(rules); // throws on duplicate metric ids
        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.MetricId), $"rule {rule.Id} has no metricId");
            Assert.NotEmpty(rule.Bands);
            foreach (var band in rule.Bands)
            {
                Assert.True(Enum.TryParse<Grade>(band.Grade, true, out _), $"rule {rule.Id} has invalid grade '{band.Grade}'");
            }
        }
    }
}
