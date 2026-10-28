using System.Globalization;
using System.Text.Json;

namespace PcHealth.Core.Advice;

/// <summary>
/// The cards someone chose not to see again in the window, persisted as dismissed-advice.json under
/// %APPDATA%\PcHealthCheck, beside consent.json (STEP-B-SPEC 6).
///
/// It holds only <see cref="AdvicePresentation.DismissKey"/>s and the day each card was hidden:
/// <c>{ "0123456789ABCDEF": "2026-10-28" }</c>. No drive name, letter, number or reading. The key is
/// part of a fingerprint of what the card says is wrong, so a card that gets worse has a new one and
/// comes back by itself.
///
/// Only the unelevated window reads or writes it. The headless scan, the elevated helper and the
/// audit kit never touch it, so a kit run still leaves nothing on the laptop, and the HTML report
/// and the JSON always list every card.
/// </summary>
public sealed class DismissedAdviceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public DismissedAdviceStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PcHealthCheck",
            "dismissed-advice.json");
    }

    public string FilePath { get; }

    /// <summary>
    /// The keys of the hidden cards. A missing, unreadable or corrupt file hides nothing: it fails
    /// open, the same direction as a corrupt consent file turning every check back off. An entry
    /// whose name is not a key of the right shape is ignored.
    /// </summary>
    public IReadOnlySet<string> Load() => new HashSet<string>(Entries().Keys, StringComparer.Ordinal);

    /// <summary>
    /// Remembers that this card is hidden, from <paramref name="when"/>'s day. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file cannot be
    /// written, for the window to say so.
    /// </summary>
    public void Dismiss(Recommendation card, DateTimeOffset when)
    {
        var key = AdvicePresentation.DismissKey(card)
            ?? throw new ArgumentException("A card without a fingerprint cannot be hidden.", nameof(card));
        var entries = Entries();
        entries[key] = when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, Options));
    }

    /// <summary>"Show again": deletes the file, so every hidden card shows again.</summary>
    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private SortedDictionary<string, string> Entries()
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath)) return entries;
            using var stream = File.OpenRead(FilePath);
            foreach (var (name, day) in JsonSerializer.Deserialize<Dictionary<string, string?>>(stream, Options) ?? [])
            {
                var key = name.ToUpperInvariant();
                if (key.Length == AdvicePresentation.DismissKeyLength && key.All(char.IsAsciiHexDigit)) entries[key] = day ?? "";
            }
            return entries;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SortedDictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
