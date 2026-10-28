using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PcHealth.Core.Modules;
using PcHealth.Core.Orchestration;
using PcHealth.Core.Processes;
using PcHealth.Ipc;
using PcHealth.Modules.Windows.Startup;

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
    string? Message)
{
    /// <summary>
    /// Whether an elevated helper actually gathered anything, which is what the report and the
    /// receipt mean by "elevated scan". Every <see cref="ModuleResult"/> in here arrived over the
    /// private pipe from the elevated process, so a single result is proof that the UAC prompt was
    /// accepted and the helper ran.
    ///
    /// Read this rather than deriving elevation from <see cref="Status"/>. Status is a verdict on
    /// the helper session, and a helper that hands back every result and then dies before the
    /// end-of-scan message is reported Failed — correctly, the session did break. Reading
    /// elevation from Status made that case stamp "(standard scan, no admin)" on a report holding
    /// every admin result, and print the receipt line "This scan used no administrator rights."
    /// The receipt is this app's honesty artifact; it must not understate what the scan did any
    /// more than it may overstate it.
    /// </summary>
    public bool RanElevated => Results.Count > 0;

    /// <summary>
    /// The hook both scan entry points hand the orchestrator to run between its two phases:
    /// waits for the helper, then gives its results to the benchmarks so they can see what the
    /// admin checks found. It never throws: a helper that broke leaves the benchmarks with only
    /// this process's findings, and the entry point still reports the helper's outcome itself.
    /// </summary>
    public static async Task<IReadOnlyList<ModuleResult>> ResultsOnceFinished(Task<ElevatedScanOutcome> run)
    {
        await Task.WhenAny(run).ConfigureAwait(false);
        return run.IsCompletedSuccessfully ? (await run.ConfigureAwait(false)).Results : [];
    }
}

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
            // This window's account goes with the request: the helper runs as whoever typed the
            // administrator password, which on a company laptop is often not the person signed in,
            // and the other-accounts check must know whose items this window already covered (PRIV-002).
            await channel.SendAsync(new RunModulesRequest([.. moduleIds], UserProfiles.CurrentUserSid()), ct).ConfigureAwait(false);

            var results = new List<ModuleResult>();
            List<ReceiptEntry> receipt = [];
            List<string> rejected = [];
            string? error = null;
            var reachedEndOfScan = false;

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
                        reachedEndOfScan = true;
                        goto finished;
                }
            }
        finished:
            // ReceiveAsync returns null when the pipe closes, which is exactly how a helper that
            // crashed, was killed, or exited early ends this loop: no ScanCompletedMessage, no
            // WorkerErrorMessage, just silence. Falling out of the loop that way used to arrive
            // here with `error` still null and report ElevatedScanStatus.Completed, so a scan
            // whose elevated half died halfway through was presented as a scan that finished,
            // with whatever results had already arrived standing in for all of them. The absence
            // of the end-of-scan message IS the failure, and saying so here is what lets the
            // caller mark the checks that never reported as checks that did not run.
            if (!reachedEndOfScan && error is null)
            {
                error = "The elevated helper stopped before it finished the administrator checks, " +
                        "so some of them did not run.";
            }

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
