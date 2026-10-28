using System.Text;
using System.Text.Json;
using PcHealth.Core.Advice;

namespace PcHealth.Core.Text;

/// <summary>
/// The sentences of the "What you can do" section, by key (STEP-B-SPEC 4). The English ones are
/// <c>Text/en.json</c>, embedded in this assembly; Step E adds a file per language with the same
/// keys. A sentence names its values <c>{likeThis}</c> and carries no number format: the values
/// are typed <see cref="AdviceArg"/>s, written by <see cref="AdviceFormat"/>, so a translation can
/// put them anywhere in its sentence. An entry is a sentence, or a <c>{ "one", "other" }</c> pair
/// chosen by the argument named <c>count</c>.
///
/// The file is named <c>en.json</c> and embedded with <c>WithCulture="false"</c>: a name such as
/// <c>advice.en.json</c> would be read as an English satellite resource and leave the main
/// assembly (STEP-B-SPEC risk 3). It is outside <c>Rules/</c>, and the rules loader never reads it.
/// </summary>
public sealed class AdviceCatalog
{
    private const string PluralArgument = "count";

    private static readonly Lazy<AdviceCatalog> EnglishCatalog = new(() => Load("PcHealth.Core.Text.en.json"));

    private readonly Dictionary<string, Entry> _entries;

    private AdviceCatalog(Dictionary<string, Entry> entries) => _entries = entries;

    /// <summary>The language of the data: JSON reports, the headless console and the kit read English (REMEDIES-PLAN decision 6).</summary>
    public static AdviceCatalog English => EnglishCatalog.Value;

    public IReadOnlyCollection<string> Keys => _entries.Keys;

    public bool Contains(string key) => _entries.ContainsKey(key);

    /// <summary>Every form of an entry as written in the file: one sentence, or its "one" and "other" forms.</summary>
    public IReadOnlyList<string> Templates(string key)
    {
        var entry = _entries[key];
        return entry.Text is { } text ? [text] : [entry.One!, entry.Other!];
    }

    /// <summary>
    /// The sentence with its values filled in, or null when an argument it names is missing or has no
    /// value: a sentence is never printed with a gap or a made-up 0. A key that is not in the catalog
    /// is a fault in the code and throws.
    /// </summary>
    public string? Render(string key, IReadOnlyDictionary<string, AdviceArg> args)
    {
        if (!_entries.TryGetValue(key, out var entry))
            throw new KeyNotFoundException($"The advice catalog has no sentence '{key}'.");
        var template = entry.Text;
        if (template is null)
        {
            if (!args.TryGetValue(PluralArgument, out var count) || count.Number is not { } n) return null;
            template = n == 1 ? entry.One : entry.Other;
        }
        return Fill(template!, args);
    }

    /// <summary>One line of a card, made from a key and its values; null when it cannot be printed (see <see cref="Render"/>).</summary>
    public AdviceText? Text(AdviceLineRole role, string key, params (string Name, AdviceArg Value)[] args)
    {
        var values = new Dictionary<string, AdviceArg>(StringComparer.Ordinal);
        foreach (var (name, value) in args) values[name] = value;
        return Render(key, values) is { } text ? new AdviceText(role, key, values, text) : null;
    }

    /// <summary>"a", "a and b", "a, b and c", with the catalog's own separators.</summary>
    public string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(Templates("list.sep")[0], items.Take(items.Count - 1)) + Templates("list.and")[0] + items[^1],
    };

    /// <summary>Reads a catalog file's text. Public for tests, which check the rendering on catalogs of their own.</summary>
    public static AdviceCatalog Parse(string json)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = JsonDocument.Parse(json, options);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("An advice catalog is one JSON object of key to sentence.");

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var entry = property.Value.ValueKind switch
            {
                JsonValueKind.String => new Entry(property.Value.GetString(), null, null),
                JsonValueKind.Object when property.Value.TryGetProperty("one", out var one) && one.ValueKind == JsonValueKind.String
                                       && property.Value.TryGetProperty("other", out var other) && other.ValueKind == JsonValueKind.String
                                       && property.Value.EnumerateObject().Count() == 2 =>
                    new Entry(null, one.GetString(), other.GetString()),
                _ => throw new InvalidDataException($"Advice sentence '{property.Name}' is neither a string nor a {{ \"one\", \"other\" }} pair."),
            };
            if (!entries.TryAdd(property.Name, entry))
                throw new InvalidDataException($"Advice sentence '{property.Name}' is in the catalog twice.");
        }
        return new AdviceCatalog(entries);
    }

    private static AdviceCatalog Load(string resourceName)
    {
        using var stream = typeof(AdviceCatalog).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded advice catalog missing: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Puts each value in place of its <c>{name}</c>. Null when one is missing or has no value, or a brace is unclosed.</summary>
    private static string? Fill(string template, IReadOnlyDictionary<string, AdviceArg> args)
    {
        var text = new StringBuilder(template.Length + 16);
        var at = 0;
        while (at < template.Length)
        {
            var open = template.IndexOf('{', at);
            if (open < 0)
            {
                text.Append(template, at, template.Length - at);
                break;
            }
            var close = template.IndexOf('}', open + 1);
            if (close < 0) return null;
            if (!args.TryGetValue(template[(open + 1)..close], out var arg) || AdviceFormat.Format(arg) is not { } value) return null;
            text.Append(template, at, open - at).Append(value);
            at = close + 1;
        }
        return text.ToString();
    }

    private sealed record Entry(string? Text, string? One, string? Other);
}
