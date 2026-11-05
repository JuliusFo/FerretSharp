using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Settings;

/// <summary>
/// Text keys to lists of tables in the settings: the tables no way between two tables goes through, per connection id
/// (WP-74). Owner and name stay apart – a quoted name may hold a dot. Compared by content, so <see cref="AppSettings"/>
/// stays a value; in <c>settings.json</c> a plain object (<c>{ "&lt;id&gt;": [ { "owner": "APP", "name": "BENUTZER" } ] }</c>).
/// Keys ordered ordinally, the tables of a key by owner and name, each once.
/// </summary>
[JsonConverter(typeof(TableListMapConverter))]
public sealed class TableListMap : IEquatable<TableListMap>
{
    public static readonly TableListMap Empty = new(ImmutableSortedDictionary<string, ImmutableArray<TableRef>>.Empty);

    private static readonly IComparer<TableRef> ByName = Comparer<TableRef>.Create((a, b) =>
        string.CompareOrdinal(a.Owner, b.Owner) is var byOwner and not 0 ? byOwner : string.CompareOrdinal(a.Name, b.Name));

    private readonly ImmutableSortedDictionary<string, ImmutableArray<TableRef>> _entries;

    private TableListMap(ImmutableSortedDictionary<string, ImmutableArray<TableRef>> entries) => _entries = entries;

    public static TableListMap From(IEnumerable<KeyValuePair<string, IEnumerable<TableRef>>> entries)
    {
        var map = Empty;
        foreach (var (key, tables) in entries)
        {
            map = map.With(key, tables);
        }

        return map;
    }

    public IEnumerable<string> Keys => _entries.Keys;

    public int Count => _entries.Count;

    /// <summary>The tables of <paramref name="key"/>; empty for none.</summary>
    public IReadOnlySet<TableRef> Of(string key) =>
        _entries.TryGetValue(key, out var tables) ? tables.ToHashSet() : new HashSet<TableRef>();

    /// <summary>The map with the tables of <paramref name="key"/> replaced; without the key if there are none.</summary>
    public TableListMap With(string key, IEnumerable<TableRef> tables)
    {
        var list = tables.Where(t => t is { Owner: not null, Name: not null }).Distinct().Order(ByName).ToImmutableArray();
        return list.IsEmpty ? Without(key) : new(_entries.SetItem(key, list));
    }

    public TableListMap Without(string key) => _entries.ContainsKey(key) ? new(_entries.Remove(key)) : this;

    public bool Equals(TableListMap? other) =>
        other is not null && _entries.Count == other._entries.Count
        && _entries.All(e => other._entries.TryGetValue(e.Key, out var tables) && tables.SequenceEqual(e.Value));

    public override bool Equals(object? obj) => Equals(obj as TableListMap);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, tables) in _entries)
        {
            hash.Add(key);
            foreach (var table in tables)
            {
                hash.Add(table);
            }
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _entries.Select(e => $"{e.Key}=[{string.Join(" ", e.Value)}]"));

    private sealed class TableListMapConverter : JsonConverter<TableListMap>
    {
        /// <summary>A list or a table set to null by hand reads as none, not as a crash at start.</summary>
        public override TableListMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Dictionary<string, List<TableRef?>?>>(ref reader, options) is { } entries
                ? From(entries.Select(e => new KeyValuePair<string, IEnumerable<TableRef>>(e.Key, (e.Value ?? []).OfType<TableRef>())))
                : Empty;

        public override void Write(Utf8JsonWriter writer, TableListMap value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value._entries.ToDictionary(e => e.Key, e => e.Value.ToList()), options);
    }
}
