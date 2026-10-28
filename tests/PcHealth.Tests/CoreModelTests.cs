using PcHealth.Core.Consent;
using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Reporting;
using Xunit;

namespace PcHealth.Tests;

public class CapabilityHashTests
{
    private static ModuleManifest Manifest(bool admin = false, WriteSpec? writes = null, bool internet = false) => new()
    {
        Id = "test.module",
        Name = "Test",
        Description = "d",
        Reads = ["x"],
        NeedsAdmin = admin,
        Writes = writes,
        NeedsInternet = internet,
    };

    [Fact]
    public void Hash_is_stable_for_identical_capabilities()
    {
        Assert.Equal(Manifest().CapabilityHash, Manifest().CapabilityHash);
    }

    [Fact]
    public void Hash_changes_when_any_capability_changes()
    {
        var baseline = Manifest().CapabilityHash;
        Assert.NotEqual(baseline, Manifest(admin: true).CapabilityHash);
        Assert.NotEqual(baseline, Manifest(writes: new WriteSpec("a file", 100, "deleted")).CapabilityHash);
        Assert.NotEqual(baseline, Manifest(internet: true).CapabilityHash);
    }

    [Fact]
    public void Display_strings_do_not_affect_the_hash()
    {
        var a = Manifest() with { Description = "one" };
        // Reads too: a consent row reworded to name what the check really reads asks nobody again.
        var b = Manifest() with { Description = "two", Name = "Other", Reads = ["x", "and y"] };
        Assert.Equal(a.CapabilityHash, b.CapabilityHash);
    }
}

public class ConsentStoreTests
{
    [Fact]
    public void Consent_is_not_current_when_capability_hash_changed()
    {
        var consent = new ModuleConsent { Enabled = true, LastConsentedCapabilityHash = "OLDHASH" };
        Assert.False(ConsentStore.IsConsentCurrent(consent, "NEWHASH"));
        Assert.True(ConsentStore.IsConsentCurrent(consent, "OLDHASH"));
        Assert.False(ConsentStore.IsConsentCurrent(new ModuleConsent { Enabled = false, LastConsentedCapabilityHash = "H" }, "H"));
        Assert.False(ConsentStore.IsConsentCurrent(null, "H"));
    }

    [Fact]
    public void Round_trips_through_disk_and_survives_corruption()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pchealth-consent-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConsentStore(path);
            store.Save(new Dictionary<string, ModuleConsent>
            {
                ["m1"] = new() { Enabled = true, LastConsentedCapabilityHash = "H1", Timestamp = DateTimeOffset.Now },
            });
            var loaded = store.Load();
            Assert.True(loaded["m1"].Enabled);
            Assert.Equal("H1", loaded["m1"].LastConsentedCapabilityHash);

            File.WriteAllText(path, "{ not json !!");
            Assert.Empty(store.Load()); // corrupt file = everything defaults back to OFF
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ScanReportTests
{
    [Fact]
    public void Worst_grade_ordering()
    {
        Assert.Equal(Grade.Bad, ScanReport.Worst([Grade.Good, Grade.Bad, Grade.Normal]));
        Assert.Equal(Grade.Normal, ScanReport.Worst([Grade.Good, Grade.Normal, Grade.Unknown]));
        Assert.Equal(Grade.Good, ScanReport.Worst([Grade.Good, Grade.Unknown]));
        Assert.Equal(Grade.Unknown, ScanReport.Worst([Grade.Unknown]));
        Assert.Equal(Grade.Unknown, ScanReport.Worst([]));
    }
}
