using PcHealth.Modules.Windows.Support;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The top memory and processor users named in the report (feature gap 5). Names only, totals per
/// program, top five: a browser running as 29 processes is one entry, not 29.
/// </summary>
public sealed class ProcessSummaryTests
{
    private const long Gb = 1L << 30;

    [Fact]
    public void Instances_of_one_program_are_summed_and_counted()
    {
        var text = ProcessSummary.TopByMemory([("brave", 3 * Gb), ("brave", 2 * Gb), ("Code", 4 * Gb), ("dwm", Gb / 2)]);

        Assert.StartsWith("brave ×2 5.0 GB; Code 4.0 GB; dwm 0.5 GB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_top_five_are_named()
    {
        var text = ProcessSummary.TopByMemory(Enumerable.Range(1, 9).Select(i => ($"app{i}", i * Gb)));

        Assert.Equal(5, text.Split("; ").Length);
        Assert.StartsWith("app9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("app4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_names_differing_only_in_case_are_one_program() =>
        Assert.StartsWith("Code ×2", ProcessSummary.TopByMemory([("Code", Gb), ("code", Gb)]), StringComparison.Ordinal);

    [Fact]
    public void Processor_time_is_shown_in_hours_or_minutes()
    {
        var text = ProcessSummary.TopByCpu([("MsMpEng", TimeSpan.FromHours(2.4)), ("rustdesk", TimeSpan.FromMinutes(35)), ("idle-ish", TimeSpan.FromMilliseconds(300))]);

        Assert.Equal("MsMpEng 2.4 h; rustdesk 35 min", text);  // under a second is left out
    }

    [Fact]
    public void Nothing_readable_gives_no_text() =>
        Assert.Equal("", ProcessSummary.TopByCpu([]));
}
