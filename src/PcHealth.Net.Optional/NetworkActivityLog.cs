namespace PcHealth.Net.Optional;

/// <summary>One outbound request this app made, with what it was for.</summary>
public sealed record NetworkRequestRecord(DateTimeOffset At, string Host, string Purpose, int? StatusCode);

/// <summary>
/// The running count of every network request this app has made, kept so the receipt can
/// state a number that is checked rather than asserted.
///
/// The app's promise is that scans never touch the network and that internet features only
/// run when the user starts them. A promise is worth more when the app can show its own
/// ledger — and if a bug ever made an unexpected request, this is what would reveal it.
/// Every request in this assembly must pass through here.
/// </summary>
public static class NetworkActivityLog
{
    private static readonly List<NetworkRequestRecord> Entries = [];
    private static readonly Lock Gate = new();

    public static void Record(string host, string purpose, int? statusCode)
    {
        lock (Gate) { Entries.Add(new NetworkRequestRecord(DateTimeOffset.Now, host, purpose, statusCode)); }
    }

    public static int Count
    {
        get { lock (Gate) { return Entries.Count; } }
    }

    public static IReadOnlyList<NetworkRequestRecord> Snapshot()
    {
        lock (Gate) { return [.. Entries]; }
    }
}
