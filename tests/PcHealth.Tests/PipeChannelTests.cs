using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Ipc;
using Xunit;

namespace PcHealth.Tests;

public class PipeChannelTests
{
    private static async Task<PipeMessage?> RoundTripAsync(PipeMessage message)
    {
        using var stream = new MemoryStream();
        await new PipeChannel(stream).SendAsync(message);
        stream.Position = 0;
        return await new PipeChannel(stream).ReceiveAsync();
    }

    [Fact]
    public async Task Round_trips_a_module_request()
    {
        var result = await RoundTripAsync(new RunModulesRequest(["disk.smart", "backup.posture"]));
        var request = Assert.IsType<RunModulesRequest>(result);
        Assert.Equal(["disk.smart", "backup.posture"], request.ModuleIds);
    }

    [Fact]
    public async Task Round_trips_module_results_with_typed_metric_values()
    {
        var original = new ModuleCompletedMessage(new ModuleResult
        {
            ModuleId = "disk.smart",
            Status = ModuleStatus.Completed,
            Elapsed = TimeSpan.FromSeconds(2.5),
            Notes = ["a note"],
            Metrics =
            [
                new Metric("smart.pending_sectors", "Pending sectors", 4d, HealthCategory.Storage,
                    Unit: "sectors", Detail: "detail", Subject: "Disk 0"),
                new Metric("smart.predict_failure", "Self-assessment", true, HealthCategory.Storage),
                new Metric("integrity.image_health", "Component store", "Healthy", HealthCategory.SoftwareOs, InfoOnly: true),
            ],
        });

        var message = Assert.IsType<ModuleCompletedMessage>(await RoundTripAsync(original));
        var metrics = message.Result.Metrics;

        Assert.Equal(ModuleStatus.Completed, message.Result.Status);
        Assert.Equal(TimeSpan.FromSeconds(2.5), message.Result.Elapsed);
        Assert.Equal(["a note"], message.Result.Notes);
        Assert.Equal(4d, metrics[0].Value.Number);
        Assert.Equal("Disk 0", metrics[0].Subject);
        Assert.True(metrics[1].Value.Flag);
        Assert.Equal("Healthy", metrics[2].Value.Text);
        Assert.True(metrics[2].InfoOnly);
    }

    [Fact]
    public async Task Round_trips_the_receipt_with_its_entry_types()
    {
        var original = new ScanCompletedMessage(
        [
            new ProcessReceipt(DateTimeOffset.Now, "disk.reliability", "fsutil", "dirty query C:", 0),
            new FileWriteReceipt(DateTimeOffset.Now, "battery", @"C:\temp\battery.xml", 1024, "battery report", true),
        ], ["unknown.module"]);

        var message = Assert.IsType<ScanCompletedMessage>(await RoundTripAsync(original));

        var process = Assert.IsType<ProcessReceipt>(message.Receipt[0]);
        Assert.Equal("fsutil", process.FileName);
        Assert.Equal(0, process.ExitCode);
        var file = Assert.IsType<FileWriteReceipt>(message.Receipt[1]);
        Assert.True(file.Deleted);
        Assert.Equal(["unknown.module"], message.RejectedModuleIds);
    }

    [Fact]
    public async Task Returns_null_at_end_of_stream()
    {
        using var empty = new MemoryStream();
        Assert.Null(await new PipeChannel(empty).ReceiveAsync());
    }

    [Fact]
    public async Task Rejects_a_bogus_length_header()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]); // ~2 GB claim
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipeChannel(stream).ReceiveAsync());
    }

    [Fact]
    public async Task Throws_when_the_peer_dies_mid_message()
    {
        using var stream = new MemoryStream([10, 0, 0, 0, 1, 2, 3]); // header promises 10 bytes, 3 present
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => new PipeChannel(stream).ReceiveAsync());
    }
}
