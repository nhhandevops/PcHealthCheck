using PcHealth.Modules.Windows.Modules;
using Xunit;

namespace PcHealth.Tests;

public class ProductStateDecoderTests
{
    [Theory]
    // Real-world Security Center productState values (0xWWSSDD scheme).
    [InlineData(0x61100u, true, true)]   // 397568 — enabled, definitions current (typical Defender active)
    [InlineData(0x60100u, false, true)]  // 393472 — disabled/passive, definitions current
    [InlineData(0x61110u, true, false)]  // 397584 — enabled, definitions out of date
    [InlineData(0x60110u, false, false)] // disabled and out of date
    public void Decodes_known_product_states(uint state, bool expectedEnabled, bool expectedUpToDate)
    {
        var decoded = ProductStateDecoder.Decode(state);
        Assert.Equal(expectedEnabled, decoded.Enabled);
        Assert.Equal(expectedUpToDate, decoded.UpToDate);
    }
}
