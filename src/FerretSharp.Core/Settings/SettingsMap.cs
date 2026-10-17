using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FerretSharp.Core.Settings;

/// <summary>
/// Text keys to text values in the settings: changed shortcuts (WP-25, action name → keys) and the audit template per
/// connection group (WP-51). Compared by content, so <see cref="AppSettings"/> stays a value; in <c>settings.json</c> a
/// plain object (<c>{ "Flush": "ctrl+shift+s" }</c>, <c>{ "ERP": "C:\\repo\\audit.sql" }</c>).
/// Keys are kept as given, ordered ordinally; looking one up by another rule (groups ignore case) is the caller's.
/// </summary>
[JsonConverter(typeof(SettingsMapConverter))]
public sealed class SettingsMap : IReadOnlyDictionary<string, string>, IEquatable<SettingsMap>
{
    public static readonly SettingsMap Empty = new(ImmutableSortedDictionary<string, string>.Empty);

    private readonly ImmutableSortedDictionary<string, string> _entries;

    private SettingsMap(ImmutableSortedDictionary<string, string> entries) => _entries = entries;

    public static SettingsMap From(IEnumerable<KeyValuePair<string, string>> entries) =>
        new(entries.ToImmutableSortedDictionary(StringComparer.Ordinal));

    /// <summary>The map with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    public SettingsMap With(string key, string value) => new(_entries.SetItem(key, value));

    /// <summary>The map without <paramref name="key"/>.</summary>
    public SettingsMap Without(string key) => new(_entries.Remove(key));

    public int Count => _entries.Count;

    public IEnumerable<string> Keys => _entries.Keys;

    public IEnumerable<string> Values => _entries.Values;

    public string this[string key] => _entries[key];

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _entries.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(SettingsMap? other) => other is not null && _entries.SequenceEqual(other._entries);

    public override bool Equals(object? obj) => Equals(obj as SettingsMap);

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

    private sealed class SettingsMapConverter : JsonConverter<SettingsMap>
    {
        public override SettingsMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) is { } entries ? From(entries) : Empty;

        public override void Write(Utf8JsonWriter writer, SettingsMap value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value._entries.ToDictionary(), options);
    }
}
