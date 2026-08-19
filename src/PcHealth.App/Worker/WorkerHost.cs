using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;
using PcHealth.Ipc;
using PcHealth.Modules.Windows;
using PcHealth.Modules.Windows.Support;

namespace PcHealth.App.Worker;

/// <summary>
/// The elevated half: `PcHealthCheck.exe --worker --pipe &lt;name&gt;`.
///
/// Security posture — the worker holds admin rights, so it treats everything arriving
/// over the pipe as an untrusted REQUEST:
///  1. It only ever runs modules from its own compiled-in registry, matched by id;
///     unknown ids are rejected and reported back, never executed.
///  2. It only runs modules that declare NeedsAdmin — there is no path to make the
///     elevated process do work the UI could have done unprivileged.
///  3. Every module in that registry is read-only by design. Should a future module
///     declare a write, the worker asks the user itself, on its own desktop, before
///     doing it — the UI cannot consent on the user's behalf across this boundary.
/// </summary>
internal static partial class WorkerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var pipeName = GetArgument(args, "--pipe");
        if (string.IsNullOrWhiteSpace(pipeName)) return 2;

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(20_000).ConfigureAwait(false);

            var channel = new PipeChannel(client);
            if (await channel.ReceiveAsync().ConfigureAwait(false) is not RunModulesRequest request)
            {
                await channel.SendAsync(new WorkerErrorMessage("Unexpected first message on the control channel.")).ConfigureAwait(false);
                return 3;
            }

            var registry = new WindowsModuleProvider().CreateModules()
                .Where(m => m.Manifest.NeedsAdmin)
                .ToDictionary(m => m.Manifest.Id, StringComparer.Ordinal);

            var approved = new List<IHealthModule>();
            var rejected = new List<string>();
            foreach (var id in request.ModuleIds.Distinct(StringComparer.Ordinal))
            {
                if (registry.TryGetValue(id, out var module)) approved.Add(module);
                else rejected.Add(id);
            }

            // Structural guard for future write-capable modules: the elevated process
            // confirms with the user directly rather than trusting the UI's consent.
            var writers = approved.Where(m => m.Manifest.Writes is not null).ToList();
            if (writers.Count > 0 && !ConfirmWrites(writers))
            {
                approved.RemoveAll(m => m.Manifest.Writes is not null);
                rejected.AddRange(writers.Select(m => m.Manifest.Id));
            }

            var receipt = new ReceiptLog();
            var context = new ModuleContext
            {
                Processes = new ProcessRunner(receipt),
                Receipt = receipt,
                AppTempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHealthCheck", "temp"),
                IsElevated = Elevation.IsElevated(),
            };

            for (var i = 0; i < approved.Count; i++)
            {
                var module = approved[i];
                var manifest = module.Manifest;
                await channel.SendAsync(new ProgressMessage(manifest.Id, manifest.Name, i + 1, approved.Count, "running")).ConfigureAwait(false);

                ModuleResult result;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    result = await module.RunAsync(context, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    result = new ModuleResult
                    {
                        ModuleId = manifest.Id,
                        Status = ModuleStatus.Failed,
                        Error = $"{e.GetType().Name}: {e.Message}",
                        Elapsed = stopwatch.Elapsed,
                    };
                }

                await channel.SendAsync(new ModuleCompletedMessage(result)).ConfigureAwait(false);
            }

            await channel.SendAsync(new ScanCompletedMessage([.. receipt.Snapshot()], rejected)).ConfigureAwait(false);
            return 0;
        }
        catch (TimeoutException)
        {
            return 4; // UI went away before we connected
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or EndOfStreamException)
        {
            return 5;
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool ConfirmWrites(List<IHealthModule> writers)
    {
        var lines = string.Join("\n", writers.Select(m =>
            $"• {m.Manifest.Name}: {m.Manifest.Writes!.PathDescription} " +
            $"(up to {m.Manifest.Writes.MaxBytes / 1024:N0} KB, {m.Manifest.Writes.Lifetime})"));
        var text = "PC Health Check is running with administrator rights and is about to write:\n\n" +
                   lines + "\n\nAllow these writes?";
        const uint MbYesNo = 0x00000004, MbIconWarning = 0x00000030, MbTopMost = 0x00040000, MbSetForeground = 0x00010000;
        const int IdYes = 6;
        return MessageBoxW(IntPtr.Zero, text, "PC Health Check — confirm write access",
            MbYesNo | MbIconWarning | MbTopMost | MbSetForeground) == IdYes;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
