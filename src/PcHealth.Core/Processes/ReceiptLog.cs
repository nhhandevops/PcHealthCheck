namespace PcHealth.Core.Processes;

/// <summary>
/// The post-scan audit trail: every external process the app spawned (with its full
/// command line) and every file it wrote. Rendered as the receipt panel after each scan,
/// alongside the standing "network connections: 0" statement — consent as an auditable
/// log, not just a gate.
/// </summary>
public sealed class ReceiptLog
{
    private readonly List<ReceiptEntry> _entries = [];
    private readonly Lock _lock = new();

    public void Add(ReceiptEntry entry)
    {
        lock (_lock) { _entries.Add(entry); }
    }

    public void MarkFileDeleted(string path)
    {
        lock (_lock)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i] is FileWriteReceipt f && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _entries[i] = f with { Deleted = true };
                }
            }
        }
    }

    public IReadOnlyList<ReceiptEntry> Snapshot()
    {
        lock (_lock) { return [.. _entries]; }
    }
}

[System.Text.Json.Serialization.JsonDerivedType(typeof(ProcessReceipt), "process")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(FileWriteReceipt), "fileWrite")]
public abstract record ReceiptEntry(DateTimeOffset At, string ModuleId);

public sealed record ProcessReceipt(DateTimeOffset At, string ModuleId, string FileName, string Arguments, int? ExitCode)
    : ReceiptEntry(At, ModuleId);

public sealed record FileWriteReceipt(DateTimeOffset At, string ModuleId, string Path, long? Bytes, string Purpose, bool Deleted)
    : ReceiptEntry(At, ModuleId);
