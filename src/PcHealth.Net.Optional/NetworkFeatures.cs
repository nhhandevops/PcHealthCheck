namespace PcHealth.Net.Optional;

/// <summary>
/// Placeholder for M4. Every internet-touching feature (VirusTotal hash lookup,
/// ClamAV signature download, manual release check) will live in this assembly
/// and nowhere else, and is only ever invoked after an explicit, per-feature,
/// labeled user consent. The rest of the solution must not reference System.Net.
/// </summary>
public static class NetworkFeatures
{
    public const bool ImplementedYet = false;
}
