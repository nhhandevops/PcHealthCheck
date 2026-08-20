using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace PcHealth.Net.Optional.VirusTotal;

/// <summary>
/// Looks up file hashes on VirusTotal — the only code in this solution that opens a socket.
///
/// It sends a SHA-256 and nothing else. The file itself is never uploaded: a hash reveals
/// that this machine holds a file with those exact bytes, which is already more than the
/// rest of the app ever discloses, and uploading the file could disclose its contents.
///
/// The API key belongs to the user, is passed in per run, and is never written to disk or
/// embedded in the build.
/// </summary>
public sealed class VirusTotalClient : IDisposable
{
    public const string Host = "www.virustotal.com";

    /// <summary>The free tier allows four lookups a minute; 16 s apart stays inside it.</summary>
    public static readonly TimeSpan FreeTierInterval = TimeSpan.FromSeconds(16);

    private const string FilesEndpoint = "https://www.virustotal.com/api/v3/files/";

    private readonly HttpClient _http;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public VirusTotalClient(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("x-apikey", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PcHealthCheck/0.1 (+local diagnostic tool)");
    }

    public async Task<FileReputation> LookupAsync(
        string sha256, string displayName, CancellationToken ct = default)
    {
        await WaitForQuotaAsync(ct).ConfigureAwait(false);

        try
        {
            using var response = await _http
                .GetAsync(new Uri(FilesEndpoint + sha256.ToLowerInvariant()), ct)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            NetworkActivityLog.Record(Host, $"reputation lookup for {displayName}", status);

            var body = status == 200 ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
            return VirusTotalResponseReader.Read(sha256, displayName, status, body);
        }
        catch (HttpRequestException e)
        {
            NetworkActivityLog.Record(Host, $"reputation lookup for {displayName} (failed)", null);
            return VirusTotalResponseReader.Read(sha256, displayName, 0,
                null) with
            {
                Summary = $"Could not reach VirusTotal ({e.Message}). Nothing is known about this file — " +
                          "that is not the same as it being clean.",
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            NetworkActivityLog.Record(Host, $"reputation lookup for {displayName} (timed out)", null);
            return VirusTotalResponseReader.Read(sha256, displayName, 0, null) with
            {
                Summary = "VirusTotal did not answer within 30 seconds, so nothing is known about this file.",
            };
        }
    }

    /// <summary>
    /// Spaces requests so the free tier is never exceeded. Being throttled would return 429s
    /// that read like "not checked" for the rest of the run.
    /// </summary>
    private async Task WaitForQuotaAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.Now - _lastRequest;
        if (_lastRequest != DateTimeOffset.MinValue && since < FreeTierInterval)
        {
            await Task.Delay(FreeTierInterval - since, ct).ConfigureAwait(false);
        }
        _lastRequest = DateTimeOffset.Now;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Runs the chosen lookups one at a time, reporting progress as it goes.</summary>
public sealed class ReputationCheck(string apiKey)
{
    public async Task<IReadOnlyList<FileReputation>> RunAsync(
        IReadOnlyList<ReputationCandidate> selected,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var client = new VirusTotalClient(apiKey);
        var results = new List<FileReputation>(selected.Count);

        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = selected[i];
            progress?.Report(
                $"Checking {(i + 1).ToString(CultureInfo.InvariantCulture)} of " +
                $"{selected.Count.ToString(CultureInfo.InvariantCulture)}: {candidate.Name}…");
            results.Add(await client.LookupAsync(candidate.Sha256, candidate.Name, ct).ConfigureAwait(false));
        }

        return results;
    }
}
