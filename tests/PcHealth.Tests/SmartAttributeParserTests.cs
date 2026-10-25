using PcHealth.Modules.Windows.Storage;
using Xunit;

namespace PcHealth.Tests;

public class SmartAttributeParserTests
{
    /// <summary>Builds a 512-byte ATA SMART READ DATA buffer the way a drive reports it.</summary>
    private static byte[] BuildBuffer(params (int Id, int Current, int Worst, long Raw)[] attributes)
    {
        var buffer = new byte[512];
        buffer[0] = 0x10; // revision
        for (var i = 0; i < attributes.Length; i++)
        {
            var (id, current, worst, raw) = attributes[i];
            var offset = 2 + (i * 12);
            buffer[offset] = (byte)id;
            buffer[offset + 1] = 0x03; // status flags
            buffer[offset + 3] = (byte)current;
            buffer[offset + 4] = (byte)worst;
            for (var b = 0; b < 6; b++) buffer[offset + 5 + b] = (byte)((raw >> (8 * b)) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void Parses_ids_values_and_little_endian_raw()
    {
        var attributes = SmartAttributeParser.Parse(BuildBuffer(
            (5, 100, 100, 3),
            (197, 200, 190, 1),
            (199, 200, 200, 0)));

        Assert.Equal(3, attributes.Count);
        var reallocated = attributes.Single(a => a.Id == 5);
        Assert.Equal("Reallocated sectors", reallocated.Name);
        Assert.Equal(100, reallocated.Current);
        Assert.Equal(100, reallocated.Worst);
        Assert.Equal(3, reallocated.Raw);
        Assert.Equal(1, attributes.Single(a => a.Id == 197).MeaningfulRaw);
    }

    [Fact]
    public void Raw_value_spans_all_six_bytes()
    {
        // 0x0000_0002_0000_0001 across the 6 raw bytes.
        var attributes = SmartAttributeParser.Parse(BuildBuffer((241, 100, 100, 0x0000020000000001)));
        Assert.Equal(0x0000020000000001, attributes.Single().Raw);
    }

    [Fact]
    public void Temperature_uses_only_the_low_byte()
    {
        // Drives pack min/max into the upper raw bytes; the current reading is byte 0.
        var raw = 38 | (20L << 16) | (45L << 32);
        var attribute = SmartAttributeParser.Parse(BuildBuffer((194, 62, 40, raw))).Single();
        Assert.Equal(38, attribute.MeaningfulRaw);
        Assert.NotEqual(38, attribute.Raw);
    }

    [Fact]
    public void Power_on_hours_masks_to_32_bits_and_timeouts_to_16()
    {
        var hours = SmartAttributeParser.Parse(BuildBuffer((9, 90, 90, 40000 | (7L << 40)))).Single();
        Assert.Equal(40000, hours.MeaningfulRaw);

        var timeouts = SmartAttributeParser.Parse(BuildBuffer((188, 100, 100, 5 | (99L << 32)))).Single();
        Assert.Equal(5, timeouts.MeaningfulRaw);
    }

    [Fact]
    public void Empty_slots_are_skipped_and_short_buffers_are_safe()
    {
        var buffer = BuildBuffer((5, 100, 100, 0));
        buffer[2] = 0; // blank out the only entry's id
        Assert.Empty(SmartAttributeParser.Parse(buffer));

        Assert.Empty(SmartAttributeParser.Parse(null));
        Assert.Empty(SmartAttributeParser.Parse([]));
        Assert.Empty(SmartAttributeParser.Parse([0x10, 0x00, 0x05]));
    }

    [Fact]
    public void Reads_at_most_thirty_attributes()
    {
        var many = Enumerable.Range(1, 40).Select(i => (i, 100, 100, (long)i)).ToArray();
        Assert.Equal(30, SmartAttributeParser.Parse(BuildBuffer(many)).Count);
    }

    [Fact]
    public void Unknown_attribute_ids_still_get_a_label()
    {
        Assert.Equal("Attribute 240", SmartAttributeParser.NameFor(240));
        Assert.Equal("Interface CRC errors", SmartAttributeParser.NameFor(199));
    }
}

/// <summary>
/// Locks the diagnosis added after the first fleet audit, where an ELEVATED scan of an NVMe-only
/// ThinkPad reported "SMART data is not accessible ... This usually means the scan is not running
/// with administrator rights" and sent the operator after a UAC problem that did not exist, while
/// the drive's health sat in the same report under the wear counters (SMART-001).
///
/// Both directions, and the quiet direction is the one that needs the most tests: the ONLY case
/// that stops being a failure is a machine where no disk sits on a bus that could ever expose an
/// ATA SMART table. A drive that should have answered, a bus this code does not recognise, a disk
/// whose bus Windows did not report, and an inventory that could not be read all keep the module
/// Failed, because letting an unread drive pass as "nothing to read here" is the bug this project
/// has shipped four times.
/// </summary>
public class SmartAvailabilityTests
{
    // MSFT_PhysicalDisk.BusType
    private const int Unknown = 0, Scsi = 1, Ata = 3, Usb = 7, Raid = 8, Sas = 10, Sata = 11, Nvme = 17;
    private const string WmiError = "ManagementException: Not supported ";

    private static IReadOnlyList<ushort?> Buses(params int?[] types) => [.. types.Select(t => (ushort?)t)];

    /// <summary>The audited T470s exactly: NVMe inside, the audit's own USB stick plugged in.</summary>
    [Fact]
    public void An_nvme_laptop_with_the_audit_stick_attached_has_no_ata_smart_to_read()
        => Assert.Equal(SmartUnavailableReason.NoAtaDrivePresent,
            SmartAvailability.Diagnose(isElevated: true, busInventoryRead: true, Buses(Nvme, Usb)));

    [Theory]
    [InlineData(Sata)]
    [InlineData(Ata)]
    [InlineData(Raid)]    // a SATA disk behind Intel RST
    [InlineData(Sas)]
    [InlineData(Scsi)]
    [InlineData(Unknown)] // Windows did not say, so assume it could have answered
    [InlineData(250)]     // a bus number this code has never heard of
    public void A_drive_that_could_have_answered_keeps_the_check_failed(int bus)
        => Assert.Equal(SmartUnavailableReason.Unexplained,
            SmartAvailability.Diagnose(isElevated: true, busInventoryRead: true, Buses(Nvme, bus)));

    [Fact]
    public void A_bus_inventory_that_could_not_be_read_proves_nothing()
        => Assert.Equal(SmartUnavailableReason.Unexplained,
            SmartAvailability.Diagnose(isElevated: true, busInventoryRead: false, Buses(Nvme)));

    [Fact]
    public void Windows_listing_no_disks_at_all_is_not_evidence_there_is_no_ata_drive()
        => Assert.Equal(SmartUnavailableReason.Unexplained,
            SmartAvailability.Diagnose(isElevated: true, busInventoryRead: true, Buses()));

    [Fact]
    public void A_disk_whose_bus_was_not_reported_counts_as_one_that_should_have_answered()
        => Assert.Equal(SmartUnavailableReason.Unexplained,
            SmartAvailability.Diagnose(isElevated: true, busInventoryRead: true, Buses(Nvme, null)));

    [Fact]
    public void Without_rights_on_a_machine_that_has_an_ata_drive_the_rights_are_the_answer()
        => Assert.Equal(SmartUnavailableReason.NotElevated,
            SmartAvailability.Diagnose(isElevated: false, busInventoryRead: true, Buses(Sata)));

    /// <summary>A drive with no SMART table has none whether or not anyone is an administrator.</summary>
    [Fact]
    public void Hardware_outranks_rights()
        => Assert.Equal(SmartUnavailableReason.NoAtaDrivePresent,
            SmartAvailability.Diagnose(isElevated: false, busInventoryRead: true, Buses(Nvme)));

    [Fact]
    public void The_nvme_note_points_at_the_check_that_did_read_the_drive_and_never_blames_uac()
    {
        var note = SmartAvailability.Explain(SmartUnavailableReason.NoAtaDrivePresent, WmiError);

        Assert.DoesNotContain("administrator", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Drive wear & error counters", note, StringComparison.Ordinal);
        Assert.Contains(WmiError, note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unexplained_note_says_the_sectors_were_not_read_and_does_not_blame_uac()
    {
        var note = SmartAvailability.Explain(SmartUnavailableReason.Unexplained, WmiError);

        Assert.DoesNotContain("not running with administrator rights", note, StringComparison.Ordinal);
        Assert.Contains("NOT read", note, StringComparison.Ordinal);
        Assert.Contains(WmiError, note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unelevated_note_is_the_one_place_administrator_rights_are_blamed()
    {
        var note = SmartAvailability.Explain(SmartUnavailableReason.NotElevated, WmiError);

        Assert.Contains("administrator rights", note, StringComparison.Ordinal);
        Assert.Contains(WmiError, note, StringComparison.Ordinal);
    }
}
