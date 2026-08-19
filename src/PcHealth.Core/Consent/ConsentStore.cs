using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHealth.Core.Consent;

/// <summary>
/// Per-module consent state, persisted as consent.json under %APPDATA%\PcHealthCheck.
/// SECURITY NOTE: this file is UI display state only. It is written by the unprivileged
/// process and must never be treated as authorization by elevated code — the elevated
/// worker (M2) validates module ids against its own compiled-in registry and shows its
/// own native confirmation for any write-flagged action.
/// </summary>
public sealed class ModuleConsent
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Capability hash at the time consent was given; a mismatch forces re-consent.</summary>
    [JsonPropertyName("lastConsentedCapabilityHash")]
    public string? LastConsentedCapabilityHash { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }
}

public sealed class ConsentStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public ConsentStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PcHealthCheck",
            "consent.json");
    }

    public Dictionary<string, ModuleConsent> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<string, ModuleConsent>>(stream, Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable consent file means: everything defaults back to OFF.
            return [];
        }
    }

    public void Save(Dictionary<string, ModuleConsent> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(state, Options));
    }

    /// <summary>
    /// True when the stored consent still applies: enabled, and given for the exact
    /// same capability hash the module declares today.
    /// </summary>
    public static bool IsConsentCurrent(ModuleConsent? consent, string capabilityHash) =>
        consent is { Enabled: true } && string.Equals(consent.LastConsentedCapabilityHash, capabilityHash, StringComparison.Ordinal);
}
