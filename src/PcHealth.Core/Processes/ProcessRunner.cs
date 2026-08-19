using System.Diagnostics;
using System.Text;

namespace PcHealth.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs an external tool, captures stdout/stderr, and records the exact invocation
    /// in the receipt log under <paramref name="moduleId"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string moduleId,
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null,
        CancellationToken ct = default);
}

public sealed class ProcessRunner(ReceiptLog receipt) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string moduleId,
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (outputEncoding is not null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
        }

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };

        int? exitCode = null;
        var timedOut = false;
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Tool missing or blocked (locked-down or trimmed Windows installs). A module
                // that cannot run its helper should degrade to a note, not fail the whole scan.
                return new ProcessResult(-1, "", $"Could not start '{fileName}': {e.Message}", TimedOut: false);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
            }
        }
        finally
        {
            receipt.Add(new ProcessReceipt(DateTimeOffset.Now, moduleId, fileName, arguments, exitCode));
        }

        return new ProcessResult(exitCode ?? -1, stdOut.ToString(), stdErr.ToString(), timedOut);
    }
}
