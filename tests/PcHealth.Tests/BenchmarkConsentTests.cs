using System.Globalization;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Benchmarks;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// The disk benchmark is the only check in this app that writes to the user's drives, so the
/// consent row describing that write is load-bearing in a way no other manifest text is.
///
/// It has already drifted once. Two instances measuring the same drive at once was fixed by
/// giving each its own <c>bench-&lt;pid&gt;.tmp</c>, and the consent row was left promising
/// <c>bench.tmp</c> — naming a path the app no longer created. Nothing failed, no test broke, and
/// an app whose whole argument is that it discloses every write was quietly misdescribing the
/// only write it makes.
///
/// These tests tie the promise to the code that keeps it, so the next rename cannot separate them.
/// </summary>
public class BenchmarkConsentTests
{
    private static WriteSpecUnderTest DiskBenchmarkWrites()
    {
        var module = new WindowsModuleProvider().CreateModules()
            .Single(m => string.Equals(m.Manifest.Id, "bench.disk", StringComparison.Ordinal));
        var writes = module.Manifest.Writes;
        Assert.NotNull(writes);
        return new WriteSpecUnderTest(writes!.PathDescription, writes.MaxBytes, writes.Lifetime);
    }

    private sealed record WriteSpecUnderTest(string PathDescription, long MaxBytes, string Lifetime);

    [Fact]
    public void ConsentNamesTheFolderTheBenchmarkActuallyCreates()
    {
        Assert.Contains(BenchmarkCleanup.WorkingFolder, DiskBenchmarkWrites().PathDescription,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConsentNamesTheFileTheBenchmarkActuallyWrites()
    {
        // The exact defect: the consent row said "bench.tmp" while BenchmarkGate wrote
        // "bench-<pid>.tmp". Asserting on the prefix means a future rename that misses the
        // manifest fails here instead of shipping a false disclosure.
        Assert.Contains(BenchmarkGate.TestFilePrefix, DiskBenchmarkWrites().PathDescription,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConsentDoesNotPromiseTheSupersededFilename()
    {
        // "bench.tmp" is what the app wrote before per-process names existed. The sweep still
        // reclaims files by that name (older versions left them), but the consent row must not
        // describe the current write that way.
        var description = DiskBenchmarkWrites().PathDescription;
        var stale = @"\" + BenchmarkCleanup.TestFileName;
        Assert.DoesNotContain(stale, description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameTheGateGeneratesIsTheOneTheSweepReclaims()
    {
        var name = BenchmarkGate.TestFileNameFor(4242);

        Assert.StartsWith(BenchmarkGate.TestFilePrefix, name, StringComparison.Ordinal);
        Assert.EndsWith(".tmp", name, StringComparison.Ordinal);

        // The wildcard the orphan sweep uses must match what the gate produces, or a killed
        // process leaves a gigabyte behind that nothing ever reclaims.
        var wildcard = BenchmarkCleanup.TestFilePattern.Replace("*", "", StringComparison.Ordinal);
        Assert.StartsWith(wildcard.Split('.')[0], name, StringComparison.Ordinal);
    }

    [Fact]
    public void TestFileNameIsCultureInvariant()
    {
        // A process id formatted under a culture with digit grouping or non-Latin digits would
        // produce a filename the sweep's wildcard still matches but that reads as nonsense in the
        // receipt. This project parses and prints numbers on Vietnamese and German Windows too.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("bench-4242.tmp", BenchmarkGate.TestFileNameFor(4242));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ConsentStatesThatTheFileIsDeleted()
    {
        // Row D1 of the acceptance checklist: the consent row must state the path, the size, and
        // that the file is removed afterwards. The first two are covered above.
        var writes = DiskBenchmarkWrites();
        Assert.True(writes.MaxBytes > 0);
        Assert.Contains("deleted", writes.Lifetime, StringComparison.OrdinalIgnoreCase);
    }
}
