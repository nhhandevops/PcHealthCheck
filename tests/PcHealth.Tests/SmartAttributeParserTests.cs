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
