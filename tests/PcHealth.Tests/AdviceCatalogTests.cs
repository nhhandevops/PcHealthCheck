using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcHealth.Core.Advice;
using PcHealth.Core.Metrics;
using PcHealth.Core.Reporting;
using PcHealth.Core.Text;
using PcHealth.Core.Verdicts;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The advice catalog and the way its values are written (STEP-B-SPEC 4 and 7). The failure this
/// guards against has already shipped once (REP-002): a number formatted with the PC's regional
/// format, so a Vietnamese or German Windows writes "8,3" into data that is meant to be English.
/// </summary>
public sealed partial class AdviceCatalogTests
{
    private static AdviceCatalog Catalog => AdviceCatalog.English;

    /// <summary>The section's own words, which the surfaces render (STEP-B-SPEC 4, keys).</summary>
    private static readonly string[] InterfaceKeys =
    [
        "advice.section.title", "advice.section.intro", "advice.section.empty", "advice.section.incomplete",
        "advice.section.other-findings", "advice.section.more", "advice.section.hidden", "advice.section.hidden-act-now",
        "advice.section.show-hidden", "advice.section.dont-show", "advice.section.for-it", "advice.section.item-error",
        "advice.urgency.act-now", "advice.urgency.check-again", "advice.urgency.plan", "advice.urgency.advice",
        "advice.area.drive-health", "advice.area.security", "advice.area.windows", "advice.area.space",
        "advice.area.heat", "advice.area.startup",
        "list.sep", "list.and",
    ];

    /// <summary>Text the plan carries for advice whose readings are Step C's (STEP-B-SPEC 5, M5, M6, S4).</summary>
    private static readonly string[] ReservedForStepC = ["advice.m5.", "advice.m6.", "advice.s4.hard-disk", "advice.s4.low-memory"];

    private static readonly string[] BannerPhrases = ["Some checks could not finish", "not the same as being healthy"];

    /// <summary>A count for each plural form.</summary>
    private static readonly double[] OneAndMany = [1, 4];

    internal static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PcHealthCheck.slnx"))) return directory.FullName;
        }
        throw new InvalidOperationException("The repository (PcHealthCheck.slnx) is not above the test binaries.");
    }

    private static T Under<T>(CultureInfo culture, Func<T> work)
    {
        var (saved, savedUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return work();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }

    private static Dictionary<string, AdviceArg> Args(params (string Name, AdviceArg Value)[] args) =>
        args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

    // --- The file ------------------------------------------------------------------------------------

    [Fact]
    public void The_English_catalog_is_embedded_in_the_core_assembly_and_loads()
    {
        var assembly = typeof(AdviceCatalog).Assembly;

        Assert.Contains("PcHealth.Core.Text.en.json", assembly.GetManifestResourceNames());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "en", "PcHealth.Core.resources.dll")),
            "en.json was compiled into a satellite assembly: it needs WithCulture=\"false\"");
        Assert.Equal("What you can do", Catalog.Render("advice.section.title", Args()));
    }

    [Fact]
    public void The_rules_loader_reads_exactly_the_rule_files_and_never_the_catalog()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };
        var onDisk = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "PcHealth.Core", "Rules"), "*.json")
            .Sum(file => JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(file), options)!.Rules.Count);

        Assert.True(onDisk > 0);
        Assert.Equal(onDisk, RulesLoader.LoadAllEmbedded().Count);
    }

    [Fact]
    public void A_catalog_that_is_not_key_to_sentence_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse("""["a"]"""));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse("""{ "a": 1 }"""));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse("""{ "a": { "one": "x" } }"""));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse("""{ "a": { "one": "x", "other": "y", "few": "z" } }"""));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse("""{ "a": "x", "a": "y" }"""));
    }

    // --- Formatting by unit, under any regional format -----------------------------------------------

    public static TheoryData<string, AdviceArg, string?> Units => new()
    {
        { "count", AdviceArg.Count(5), "5" },
        { "large count", AdviceArg.Count(12345), "12345" },
        { "percent", AdviceArg.Percent(86), "86%" },
        { "percent with a decimal", AdviceArg.Percent(8.3), "8.3%" },
        { "percent under a band edge", AdviceArg.Percent(9.9), "9.9%" },
        { "small GB", AdviceArg.Gb(8.3), "8.3 GB" },
        { "GB", AdviceArg.Gb(104.4), "104 GB" },
        { "GB rounding up past 10", AdviceArg.Gb(9.96), "10 GB" },
        { "minutes", AdviceArg.Minutes(12), "15" },
        { "whole minutes", AdviceArg.Minutes(15), "15" },
        { "a moment", AdviceArg.Minutes(0.2), "5" },
        { "days", AdviceArg.Days(7.1), "7" },
        { "celsius", AdviceArg.Celsius(91.4), "91 °C" },
        { "date", AdviceArg.Date(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)), "2026-10-21" },
        { "words", AdviceArg.Words("C:"), "C:" },
        { "plain number", new AdviceArg(2.25, null, AdviceUnit.None), "2.25" },
        { "neither", new AdviceArg(null, null, AdviceUnit.Gb), null },
        { "both", new AdviceArg(1, "one", AdviceUnit.Count), null },
        { "not a number", AdviceArg.Gb(double.NaN), null },
        { "infinite", AdviceArg.Percent(double.PositiveInfinity), null },
        { "a date given as a number", new AdviceArg(20261021, null, AdviceUnit.Date), null },
    };

    [Theory]
    [MemberData(nameof(Units))]
    public void Every_unit_is_written_the_same_under_every_regional_format(string what, AdviceArg arg, string? expected)
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("vi-VN"), new CultureInfo("de-DE"), new CultureInfo("ar-SA") })
        {
            Assert.True(expected == Under(culture, () => AdviceFormat.Format(arg)), $"{what} under '{culture.Name}'");
        }
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("de-DE")]
    public void Every_sentence_in_the_catalog_reads_the_same_under_any_regional_format(string culture)
    {
        // Every placeholder but count gets 8.3 GB: "8,3 GB" is what a comma culture would print.
        static List<string?> RenderAll() =>
        [
            .. Catalog.Keys.Order(StringComparer.Ordinal).SelectMany(key => OneAndMany.Select(count =>
                Catalog.Render(key, Placeholders(key).ToDictionary(
                    name => name, name => name == "count" ? AdviceArg.Count(count) : AdviceArg.Gb(8.3), StringComparer.Ordinal)))),
        ];

        var invariant = Under(CultureInfo.InvariantCulture, RenderAll);
        var local = Under(new CultureInfo(culture), RenderAll);

        Assert.Equal(invariant, local);
        Assert.All(invariant, text => Assert.False(text is null || text.Contains("8,3", StringComparison.Ordinal) || text.Contains('{', StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("de-DE")]
    public void A_worked_out_section_is_byte_identical_under_any_regional_format(string culture)
    {
        IReadOnlyList<Verdict> verdicts =
        [
            AdviceEngineTests.V("smart.reallocated", 5, Grade.Bad, "MODEL-A — 240 GB", 0),
            AdviceEngineTests.V("storage.disk.used_gb", 104.4, Grade.Unknown, "C:", 0, infoOnly: true),
            AdviceEngineTests.V("updates.pending_count", 3.5, Grade.Normal),
        ];
        IAdviceItem[] items =
        [
            TestAdviceItem.CardPerFailingDrive(),
            new TestAdviceItem("B", "security", (_, _) => throw new InvalidOperationException()),
        ];
        string Json() => JsonSerializer.Serialize(AdviceEngineTests.Derive(items, verdicts));

        Assert.Equal(Under(CultureInfo.InvariantCulture, Json), Under(new CultureInfo(culture), Json));
    }

    // --- Rendering -----------------------------------------------------------------------------------

    private static readonly AdviceCatalog Sample = AdviceCatalog.Parse("""
        {
          "retired": { "one": "It has retired {count} damaged area so far.", "other": "It has retired {count} damaged areas so far." },
          "copy": "About {usedGb} is in use on {letters}.",
          "plain": "Nothing to fill in."
        }
        """);

    [Theory]
    [InlineData(1, "It has retired 1 damaged area so far.")]
    [InlineData(5, "It has retired 5 damaged areas so far.")]
    [InlineData(0, "It has retired 0 damaged areas so far.")]
    public void Plurals_follow_the_argument_named_count(double count, string expected) =>
        Assert.Equal(expected, Sample.Render("retired", Args(("count", AdviceArg.Count(count)))));

    [Fact]
    public void The_section_s_own_plurals_follow_the_count()
    {
        Assert.Equal("1 other reading is graded Normal or Action needed; its tips are in its section below.",
            Catalog.Render("advice.section.other-findings", Args(("count", AdviceArg.Count(1)))));
        Assert.Equal("4 other readings are graded Normal or Action needed; their tips are in their sections below.",
            Catalog.Render("advice.section.other-findings", Args(("count", AdviceArg.Count(4)))));
        Assert.Equal("1 check did not finish (listed above), so this is not an all-clear.",
            Catalog.Render("advice.section.incomplete", Args(("count", AdviceArg.Count(1)))));
    }

    [Fact]
    public void A_missing_argument_gives_no_sentence_never_a_gap_or_a_zero()
    {
        Assert.Equal("About 104 GB is in use on C:, D:.",
            Sample.Render("copy", Args(("usedGb", AdviceArg.Gb(104.4)), ("letters", AdviceArg.Words("C:, D:")))));

        Assert.Null(Sample.Render("copy", Args(("letters", AdviceArg.Words("C:")))));
        Assert.Null(Sample.Render("copy", Args(("usedGb", new AdviceArg(null, null, AdviceUnit.Gb)), ("letters", AdviceArg.Words("C:")))));
        Assert.Null(Sample.Render("retired", Args()));
        Assert.Null(Sample.Render("retired", Args(("count", AdviceArg.Words("five")))));
        Assert.Null(Sample.Text(AdviceLineRole.Finding, "copy", ("letters", AdviceArg.Words("C:"))));
        Assert.Equal("Nothing to fill in.", Sample.Render("plain", Args(("unused", AdviceArg.Count(3)))));
    }

    [Fact]
    public void A_key_the_catalog_does_not_have_is_a_fault_not_a_blank() =>
        Assert.Throws<KeyNotFoundException>(() => Catalog.Render("advice.no-such-key", Args()));

    [Fact]
    public void A_line_keeps_the_key_and_the_typed_arguments_it_was_made_from()
    {
        var line = Catalog.Text(AdviceLineRole.Finding, "advice.section.more", ("count", AdviceArg.Count(2)))!;

        Assert.Equal(("advice.section.more", "2 more"), (line.Key, line.Text));
        Assert.Equal(new AdviceArg(2, null, AdviceUnit.Count), line.Args["count"]);
    }

    [Fact]
    public void Lists_use_the_catalog_s_own_separators()
    {
        Assert.Equal("", Catalog.List([]));
        Assert.Equal("a", Catalog.List(["a"]));
        Assert.Equal("a and b", Catalog.List(["a", "b"]));
        Assert.Equal("a, b and c", Catalog.List(["a", "b", "c"]));
    }

    // --- What the file holds -------------------------------------------------------------------------

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();

    private static IEnumerable<string> Placeholders(string key) =>
        Catalog.Templates(key).SelectMany(t => Placeholder().Matches(t).Select(m => m.Groups[1].Value)).Distinct();

    [Fact]
    public void Every_entry_is_well_formed()
    {
        foreach (var key in Catalog.Keys)
        {
            Assert.Matches("^[a-z0-9]+(\\.[a-z0-9-]+)+$", key);
            foreach (var template in Catalog.Templates(key))
            {
                Assert.False(string.IsNullOrWhiteSpace(template), key);
                // Named placeholders only, no format strings: the numbers are formatted in code.
                Assert.All(Placeholder().Matches(template), m => Assert.Matches("^[a-z][A-Za-z0-9]*$", m.Groups[1].Value));
                Assert.DoesNotContain('{', Placeholder().Replace(template, ""));
                Assert.DoesNotContain('}', Placeholder().Replace(template, ""));
            }
            if (Catalog.Templates(key).Count == 2)
                Assert.All(Catalog.Templates(key), t => Assert.Contains("{count}", t, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_section_s_own_words_are_in_the_catalog()
    {
        foreach (var key in InterfaceKeys)
        {
            Assert.True(Catalog.Contains(key), key);
        }
        Assert.Equal("Advice about drive health could not be worked out (FormatException). The readings below are complete.",
            Catalog.Render("advice.section.item-error", Args(("area", AdviceArg.Words("drive health")), ("error", AdviceArg.Words("FormatException")))));
    }

    /// <summary>The keys the advice code names as string literals, and the prefixes it builds keys from ("advice.area.").</summary>
    private static HashSet<string> KeysNamedInCode()
    {
        var root = RepoRoot();
        var sources = new[] { Path.Combine(root, "src", "PcHealth.Core", "Advice"), Path.Combine(root, "src", "PcHealth.Core", "Text") }
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories));
        return [.. sources.SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"((?:advice|list)\\.[a-z0-9.\\-]*)\"").Select(m => m.Groups[1].Value))];
    }

    [Fact]
    public void Every_key_the_code_names_exists_and_every_key_in_the_catalog_is_used_or_reserved()
    {
        var named = KeysNamedInCode();
        Assert.Contains("advice.section.item-error", named); // the scan finds what it is looking for

        foreach (var key in named)
        {
            Assert.True(key.EndsWith('.') ? Catalog.Keys.Any(k => k.StartsWith(key, StringComparison.Ordinal)) : Catalog.Contains(key), key);
        }
        foreach (var key in Catalog.Keys)
        {
            var used = InterfaceKeys.Contains(key) || named.Contains(key)
                || named.Any(prefix => prefix.EndsWith('.') && key.StartsWith(prefix, StringComparison.Ordinal))
                || ReservedForStepC.Any(reserved => key == reserved || key.StartsWith(reserved, StringComparison.Ordinal));
            Assert.True(used, $"{key} is in en.json but nothing uses it and it is not reserved for Step C");
        }
    }

    /// <summary>What a sentence must never say. Empty when it is fine.</summary>
    internal static List<string> ContentProblems(string sentence)
    {
        var problems = new List<string>();
        if (sentence.Contains("http", StringComparison.OrdinalIgnoreCase)) problems.Add("a link: the report is offline and self-contained");
        if (sentence.Contains("INCOMPLETE", StringComparison.Ordinal)) problems.Add("the headline's INCOMPLETE");
        problems.AddRange(BannerPhrases.Where(p => sentence.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(p => "the banner's words: " + p));
        for (var at = sentence.IndexOf("ms-settings:", StringComparison.Ordinal); at >= 0; at = sentence.IndexOf("ms-settings:", at + 1, StringComparison.Ordinal))
        {
            if (!sentence[..at].EndsWith("Win+R, then ", StringComparison.Ordinal)) problems.Add("a Windows page not opened with \"Win+R, then\"");
        }
        return problems;
    }

    [Theory]
    [InlineData("Free space is graded Good above 20%.", true)]
    [InlineData("Storage Sense can do part of this (Win+R, then ms-settings:storagesense).", true)]
    [InlineData("Open ms-settings:storagesense.", false)]
    [InlineData("See https://example.org for more.", false)]
    [InlineData("Some checks could not finish, so...", false)]
    [InlineData("That is not the same as being healthy.", false)]
    [InlineData("INCOMPLETE scan.", false)]
    public void The_content_rules_tell_a_fine_sentence_from_a_bad_one(string sentence, bool fine) =>
        Assert.Equal(fine, ContentProblems(sentence).Count == 0);

    [Fact]
    public void No_sentence_links_out_borrows_the_banner_s_words_or_opens_a_Windows_page_another_way()
    {
        foreach (var key in Catalog.Keys)
        {
            foreach (var template in Catalog.Templates(key))
            {
                Assert.True(ContentProblems(template).Count == 0, $"{key}: {string.Join("; ", ContentProblems(template))}");
            }
        }
    }

    // --- The source scan -----------------------------------------------------------------------------

    /// <summary>
    /// Interpolated strings whose holes carry a format (<c>{x:0.0}</c>) and that are not formatted with
    /// the invariant culture: not the second argument of <c>string.Create(CultureInfo.InvariantCulture, …)</c>
    /// or of a StringBuilder's <c>Append(CultureInfo.InvariantCulture, …)</c>. CA1305 does not flag
    /// them, and they are REP-002's root cause. Comments are skipped.
    /// </summary>
    internal static List<string> CultureFormattedStrings(string source)
    {
        var found = new List<string>();
        var at = 0;
        while (at < source.Length)
        {
            if (source[at] == '/' && at + 1 < source.Length && source[at + 1] is '/' or '*')
            {
                var end = source[at + 1] == '/' ? source.IndexOf('\n', at) : source.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? source.Length : end + 1;
                continue;
            }
            if (source[at] == '\'')
            {
                at += source[at + 1] == '\\' ? 4 : 3;
                continue;
            }

            var start = at;
            var interpolated = false;
            var verbatim = false;
            while (at < source.Length && source[at] is '$' or '@')
            {
                interpolated |= source[at] == '$';
                verbatim |= source[at] == '@';
                at++;
            }
            if (at >= source.Length || source[at] != '"')
            {
                at = start + 1;
                continue;
            }

            var formatted = false;
            at++;
            while (at < source.Length && source[at] != '"')
            {
                if (!verbatim && source[at] == '\\') { at += 2; continue; }
                if (interpolated && source[at] == '{')
                {
                    if (at + 1 < source.Length && source[at + 1] == '{') { at += 2; continue; }
                    var depth = 0;
                    for (at++; at < source.Length; at++)
                    {
                        var c = source[at];
                        if (c is '(' or '[' or '{') depth++;
                        else if (c is ')' or ']') depth--;
                        else if (c == '}' && depth-- == 0) break;
                        else if (c == ':' && depth == 0) formatted = true;
                        else if (c == '"') at = source.IndexOf('"', at + 1); // a string inside the hole
                    }
                }
                at++;
                if (verbatim && at + 1 < source.Length && source[at] == '"' && source[at + 1] == '"') at += 2;
            }
            at++;

            var lead = source[Math.Max(0, start - 80)..start];
            if (formatted && !InvariantLead().IsMatch(lead)) found.Add(source[start..Math.Min(at, source.Length)]);
        }
        return found;
    }

    [GeneratedRegex(@"(?:string\.Create|\.Append|\.AppendLine)\(\s*CultureInfo\.InvariantCulture\s*,\s*$")]
    private static partial Regex InvariantLead();

    [Theory]
    [InlineData("var s = $\"{x:0.0} GB\";", 1)]
    [InlineData("var s = @$\"{x,8:N1}\";", 1)]
    [InlineData("var s = $\"{a} and {b:0}\";", 1)]
    [InlineData("var s = string.Create(CultureInfo.InvariantCulture, $\"{x:0.0} GB\");", 0)]
    [InlineData("sb.Append(CultureInfo.InvariantCulture, $\"<td>{x:0}</td>\");", 0)]
    [InlineData("var s = $\"{name} and {{literal:braces}}\";", 0)]
    [InlineData("var s = $\"{(ok ? a : b)}\";", 0)]
    [InlineData("var s = $\"{d[\"k:v\"]}\";", 0)]
    [InlineData("// var s = $\"{x:0.0}\";\nvar t = \"{x:0.0}\";", 0)]
    [InlineData("/* $\"{x:0.0}\" */ var c = '\"'; var s = $\"{x:0}\";", 1)]
    public void The_source_scan_finds_a_culture_formatted_string_and_nothing_else(string source, int expected) =>
        Assert.Equal(expected, CultureFormattedStrings(source).Count);

    [Fact]
    public void No_advice_code_formats_a_number_with_the_current_culture()
    {
        var root = RepoRoot();
        var files = new[] { Path.Combine(root, "src", "PcHealth.Core", "Advice"), Path.Combine(root, "src", "PcHealth.Core", "Text") }
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
            .ToList();

        Assert.Contains(files, f => f.EndsWith("AdviceFormat.cs", StringComparison.Ordinal));
        foreach (var file in files)
        {
            Assert.True(CultureFormattedStrings(File.ReadAllText(file)).Count == 0,
                $"{Path.GetFileName(file)}: {string.Join(" | ", CultureFormattedStrings(File.ReadAllText(file)))}");
        }
    }
}
