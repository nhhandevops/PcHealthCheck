using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Ipc;

namespace PcHealth.App.Worker;

public enum ElevatedScanStatus
{
    Completed,
    DeclinedByUser,
    Failed,
}

public sealed record ElevatedScanOutcome(
    ElevatedScanStatus Status,
    IReadOnlyList<ModuleResult> Results,
    IReadOnlyList<ReceiptEntry> Receipt,
    IReadOnlyList<string> RejectedModuleIds,
    string? Message);

/// <summary>
/// UI side of the split-privilege design. Creates a private named pipe, launches ONE
/// elevated copy of this exe (`--worker`) through the UAC "runas" verb, verifies that
/// the process which connected is exactly the one we launched, then asks it to run the
/// consented admin modules and streams results back.
///
/// The UI process itself never elevates: at most one UAC prompt per scan, and only when
/// the user selected an admin-badged module.
/// </summary>
public sealed partial class ElevatedWorkerSession
{
    private const int ErrorCancelled = 1223; // user clicked "No" on the UAC prompt

    public async Task<ElevatedScanOutcome> RunAsync(
        IReadOnlyList<string> moduleIds,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        if (moduleIds.Count == 0)
            return new ElevatedScanOutcome(ElevatedScanStatus.Completed, [], [], [], null);

        // Unguessable per-session pipe name; maxInstances 1 so name squatting fails loudly.
        var pipeName = $"pchealth-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var exePath = Environment.ProcessPath;
        if (exePath is null)
            return Failed("Could not determine the application path to start the elevated helper.");

        Process? worker;
        try
        {
            worker = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--worker --pipe {pipeName}",
                UseShellExecute = true,   // required for the runas verb
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedScanOutcome(ElevatedScanStatus.DeclinedByUser, [], [], [],
                "Administrator access was declined, so the admin checks were skipped. Everything else still ran.");
        }
        catch (Win32Exception e)
        {
            return Failed($"Could not start the elevated helper: {e.Message}");
        }

        if (worker is null) return Failed("The elevated helper did not start.");

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await server.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failed("The elevated helper did not connect in time.");
            }

            // TRUST BOUNDARY: only accept the process we launched ourselves.
            if (!TryGetClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != worker.Id)
            {
                return Failed("The process that connected to the private channel was not the helper we started. " +
                              "The admin checks were abandoned as a precaution.");
            }

            var channel = new PipeChannel(server);
            await channel.SendAsync(new RunModulesRequest([.. moduleIds]), ct).ConfigureAwait(false);

            var results = new List<ModuleResult>();
            List<ReceiptEntry> receipt = [];
            List<string> rejected = [];
            string? error = null;

            while (await channel.ReceiveAsync(ct).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case ProgressMessage p:
                        progress?.Report(new ScanProgress(p.ModuleId, p.ModuleName, p.Index, p.Total, p.Message));
                        break;
                    case ModuleCompletedMessage m:
                        results.Add(m.Result);
                        break;
                    case WorkerErrorMessage e:
                        error = e.Message;
                        break;
                    case ScanCompletedMessage done:
                        receipt = done.Receipt;
                        rejected = done.RejectedModuleIds;
                        goto finished;
                }
            }
        finished:
            return new ElevatedScanOutcome(
                error is null ? ElevatedScanStatus.Completed : ElevatedScanStatus.Failed,
                results, receipt, rejected, error);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or EndOfStreamException)
        {
            return Failed($"Lost contact with the elevated helper: {e.Message}");
        }
        finally
        {
            try
            {
                if (!worker.HasExited && !worker.WaitForExit(3000)) worker.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
            worker.Dispose();
        }
    }

    private static ElevatedScanOutcome Failed(string message) =>
        new(ElevatedScanStatus.Failed, [], [], [], message);

    private static bool TryGetClientProcessId(SafePipeHandle handle, out int processId)
    {
        processId = 0;
        if (!GetNamedPipeClientProcessId(handle, out var pid)) return false;
        processId = (int)pid;
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
