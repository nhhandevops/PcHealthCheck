using System.Diagnostics;
using System.IO.Pipes;
using PcHealth.Core.Modules;
using PcHealth.Ipc;
using Xunit;

namespace PcHealth.Tests;

/// <summary>
/// Drives the real worker process over the real pipe protocol (unelevated, so the admin
/// modules themselves degrade — that is fine, the point is the contract). These tests
/// guard the trust boundary: the worker must run only what its own compiled-in registry
/// contains, whatever the caller asks for.
/// </summary>
public class WorkerProtocolTests
{
    private static string? FindAppExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Debug"; // .../bin/<config>/<tfm>
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName,
                "src", "PcHealth.App", "bin", configuration, "net10.0-windows", "PcHealthCheck.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record WorkerRun(List<ModuleResult> Results, List<string> Rejected, bool CompletedCleanly);

    private static async Task<WorkerRun?> RunWorkerAsync(string[] requestedModuleIds)
    {
        var exe = FindAppExecutable();
        if (exe is null) return null; // app not built in this configuration — test skips

        var pipeName = $"pchealth-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--worker --pipe {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await server.WaitForConnectionAsync(cts.Token);

            var channel = new PipeChannel(server);
            await channel.SendAsync(new RunModulesRequest(requestedModuleIds), cts.Token);

            var results = new List<ModuleResult>();
            var rejected = new List<string>();
            var completed = false;

            while (await channel.ReceiveAsync(cts.Token) is { } message)
            {
                if (message is ModuleCompletedMessage m) results.Add(m.Result);
                else if (message is ScanCompletedMessage done)
                {
                    rejected = done.RejectedModuleIds;
                    completed = true;
                    break;
                }
            }
            return new WorkerRun(results, rejected, completed);
        }
        finally
        {
            try { if (!process.WaitForExit(5000)) process.Kill(true); } catch { /* already gone */ }
        }
    }

    [Fact]
    public async Task Worker_rejects_module_ids_that_are_not_in_its_own_registry()
    {
        var run = await RunWorkerAsync(["backup.posture", "definitely.not.a.module", "../../evil"]);
        Assert.True(run is not null, "PcHealthCheck.exe was not found — build the solution before running tests.");

        Assert.True(run!.CompletedCleanly);
        Assert.Contains("definitely.not.a.module", run.Rejected);
        Assert.Contains("../../evil", run.Rejected);
        Assert.DoesNotContain("backup.posture", run.Rejected);
        Assert.Equal(["backup.posture"], run.Results.Select(r => r.ModuleId));
    }

    [Fact]
    public async Task Worker_refuses_to_run_modules_that_do_not_require_admin()
    {
        // The elevated process must never become a way to run ordinary work with admin
        // rights, even when the (unprivileged) caller asks it to.
        var run = await RunWorkerAsync(["startup", "license", "events.errors"]);
        Assert.True(run is not null, "PcHealthCheck.exe was not found — build the solution before running tests.");

        Assert.True(run!.CompletedCleanly);
        Assert.Empty(run.Results);
        Assert.Equal(3, run.Rejected.Count);
    }

    [Fact]
    public void Worker_exits_promptly_when_no_pipe_is_waiting()
    {
        var exe = FindAppExecutable();
        Assert.True(exe is not null, "PcHealthCheck.exe was not found — build the solution before running tests.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe!,
            Arguments = $"--worker --pipe pchealth-test-nonexistent-{Guid.NewGuid():N}",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        // Must give up on its own — an elevated process may never wait indefinitely.
        Assert.True(process.WaitForExit(60_000), "The worker did not exit after failing to connect.");
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public void Worker_exits_with_an_error_code_when_told_nothing()
    {
        var exe = FindAppExecutable();
        Assert.True(exe is not null, "PcHealthCheck.exe was not found — build the solution before running tests.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe!,
            Arguments = "--worker",   // no --pipe argument at all
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        Assert.True(process.WaitForExit(30_000), "The worker did not exit when given no pipe name.");
        Assert.Equal(2, process.ExitCode);
    }
}
