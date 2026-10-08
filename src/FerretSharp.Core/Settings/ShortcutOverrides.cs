using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FerretSharp.Core.Settings;

/// <summary>
/// The shortcuts the user changed (WP-25): action name → key combination, empty for none. Compared by content, so
/// <see cref="AppSettings"/> stays a value; in <c>settings.json</c> a plain object (<c>{ "Flush": "ctrl+shift+s" }</c>).
/// </summary>
[JsonConverter(typeof(ShortcutOverridesConverter))]
public sealed class ShortcutOverrides : IReadOnlyDictionary<string, string>, IEquatable<ShortcutOverrides>
{
    public static readonly ShortcutOverrides Empty = new(ImmutableSortedDictionary<string, string>.Empty);

    private readonly ImmutableSortedDictionary<string, string> _entries;

    private ShortcutOverrides(ImmutableSortedDictionary<string, string> entries) => _entries = entries;

    public static ShortcutOverrides From(IEnumerable<KeyValuePair<string, string>> entries) =>
        new(entries.ToImmutableSortedDictionary(StringComparer.Ordinal));

    public int Count => _entries.Count;

    public IEnumerable<string> Keys => _entries.Keys;

    public IEnumerable<string> Values => _entries.Values;

    public string this[string key] => _entries[key];

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _entries.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ShortcutOverrides? other) => other is not null && _entries.SequenceEqual(other._entries);

    public override bool Equals(object? obj) => Equals(obj as ShortcutOverrides);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _entries)
        {
            hash.Add(key);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _entries.Select(e => $"{e.Key}={e.Value}"));

    private sealed class ShortcutOverridesConverter : JsonConverter<ShortcutOverrides>
    {
        public override ShortcutOverrides Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) is { } entries ? From(entries) : Empty;

        public override void Write(Utf8JsonWriter writer, ShortcutOverrides value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value._entries.ToDictionary(), options);
    }
}
