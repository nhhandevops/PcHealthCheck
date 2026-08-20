using PcHealth.Core.Processes;
using PcHealth.Modules.Windows.Security;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Runs the real thing against a folder holding one harmless file. The pure tests cover the
/// wording; this covers the wiring — that MpCmdRun is found, that the command actually sent
/// is the one the consent dialog showed, and that the run lands in the receipt.
///
/// It is what caught the duration bug: a scan finishing in 50 ms reported "1 minute(s)".
/// Where Defender is absent, the same test asserts the honest fallback instead.
/// </summary>
public class DefenderScanRunnerTests
{
    [Fact]
    public async Task A_real_folder_scan_reads_the_folder_and_changes_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pchealth-scan-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "harmless.txt");
        await File.WriteAllTextAsync(file, "nothing to see here");
        var writtenAt = File.GetLastWriteTimeUtc(file);

        try
        {
            var receipt = new ReceiptLog();
            var outcome = await new DefenderScanRunner(new ProcessRunner(receipt))
                .RunAsync(DefenderScanKind.Folder, folder, TimeSpan.FromMinutes(5));

            Assert.Equal(DefenderScanRunner.DescribeCommand(DefenderScanKind.Folder, folder), outcome.CommandLine);

            if (DefenderScanRunner.FindMpCmdRun() is null)
            {
                Assert.Contains("no scan ran", outcome.Message, StringComparison.Ordinal);
                return;
            }

            Assert.False(outcome.TimedOut);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Contains(folder, outcome.Message, StringComparison.Ordinal);
            Assert.Contains("nothing was moved, changed or deleted", outcome.Message, StringComparison.Ordinal);

            // The claim above, actually checked.
            Assert.True(File.Exists(file));
            Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(file));

            // The exact invocation, in the audit trail, under the action's own id.
            var entry = Assert.Single(receipt.Snapshot().OfType<ProcessReceipt>());
            Assert.Equal(DefenderScanRunner.ActionId, entry.ModuleId);
            Assert.Contains("-DisableRemediation", entry.Arguments, StringComparison.Ordinal);
            Assert.EndsWith("MpCmdRun.exe", entry.FileName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
