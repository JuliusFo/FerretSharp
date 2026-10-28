using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;
using Microsoft.AspNetCore.Components.Web;

namespace FerretSharp.UI.State;

/// <summary>
/// The shortcuts in effect (WP-25), from the app settings: the Shell registers the global ones, the form and the grid
/// ask for theirs, buttons and tooltips show them. Not tied to a connection – injected, unlike the scoped services.
/// </summary>
public sealed class ShortcutService : IDisposable
{
    private readonly AppSettingsService _settings;
    private ShortcutMap _map;

    public ShortcutService(AppSettingsService settings)
    {
        _settings = settings;
        _map = new ShortcutMap(settings.Current.Shortcuts, settings.Current.ConnectionShortcuts);
        settings.Changed += OnSettingsChanged;
    }

    public ShortcutMap Map => _map;

    /// <summary>A shortcut changed (on the thread that changed the settings).</summary>
    public event Action? Changed;

    /// <summary>For tooltips and messages, e.g. <c>Ctrl+S</c>; null if the action has no shortcut.</summary>
    public string? Label(ShortcutAction action) => _map.Chord(action)?.Label;

    /// <summary>For a tooltip after the text: <c> (Ctrl+S)</c>, or nothing if the action has no shortcut.</summary>
    public string Hint(ShortcutAction action) => Label(action) is { } label ? $" ({label})" : "";

    /// <summary>Whether a key event (Blazor) is the action's shortcut.</summary>
    public bool Is(ShortcutAction action, KeyboardEventArgs e) =>
        _map.Chord(action) is { } chord && chord == KeyChord.FromEvent(e.Key, e.CtrlKey, e.ShiftKey, e.AltKey);

    /// <summary>The local combos for <c>shortcuts.js</c> (<c>setLocal</c>): those grid.js handles itself.</summary>
    public Dictionary<string, string?> LocalCombos => new()
    {
        ["form"] = _map.Chord(ShortcutAction.ToggleForm)?.Text,
        ["undoEdit"] = _map.Chord(ShortcutAction.UndoEdit)?.Text,
        ["redoEdit"] = _map.Chord(ShortcutAction.RedoEdit)?.Text,
        ["copyColumn"] = _map.Chord(ShortcutAction.CopyColumnValues)?.Text,
    };

    /// <summary>The key of a connection for tooltips and lists (WP-66); null if it has none.</summary>
    public string? ConnectionKey(Guid profileId) => _map.ConnectionChord(profileId)?.Label;

    /// <summary>A connection with this key loses it: the action wins with the same key.</summary>
    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task SetAsync(ShortcutAction action, KeyChord? chord) =>
        UpdateAsync(map => (map.With(action, chord), map.ConnectionsWithout([chord])));

    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task ResetAsync(ShortcutAction action) =>
        UpdateAsync(map => (map.Without(action), map.ConnectionsWithout([ShortcutMap.Definition(action).DefaultChord])));

    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task ResetAllAsync() => UpdateAsync(map => (map.Reset(), map.ConnectionsWithout(map.Defaults)));

    /// <summary>
    /// Gives the connection the key (WP-66; null: none). An action or another connection with it loses it – the caller
    /// asked first (<see cref="ShortcutMap.CheckConnection"/>).
    /// </summary>
    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task SetConnectionAsync(Guid profileId, KeyChord? chord) =>
        UpdateAsync(map => (
            chord is not null && map.ActionFor(chord, Enum.GetValues<ShortcutScope>()) is { } action ? map.With(action, null) : map.Overrides,
            map.WithConnection(profileId, chord)));

    /// <summary>Drops the keys of connections that no longer exist (deleted); nothing is written if there are none.</summary>
    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task ForgetConnectionsExceptAsync(IReadOnlySet<Guid> existing)
    {
        bool Gone(string key) => Guid.TryParse(key, out var id) && !existing.Contains(id);
        return _map.ConnectionOverrides.Keys.Any(Gone)
            ? UpdateAsync(map => (map.Overrides, SettingsMap.From(map.ConnectionOverrides.Where(e => !Gone(e.Key)))))
            : Task.CompletedTask;
    }

    private Task UpdateAsync(Func<ShortcutMap, (SettingsMap Shortcuts, SettingsMap Connections)> change) =>
        _settings.UpdateAsync(
            s =>
            {
                var (shortcuts, connections) = change(new ShortcutMap(s.Shortcuts, s.ConnectionShortcuts));
                return s with { Shortcuts = shortcuts, ConnectionShortcuts = connections };
            },
            CancellationToken.None);

    private void OnSettingsChanged()
    {
        var current = _settings.Current;
        if (current.Shortcuts.Equals(_map.Overrides) && current.ConnectionShortcuts.Equals(_map.ConnectionOverrides))
        {
            return;
        }

        _map = new ShortcutMap(current.Shortcuts, current.ConnectionShortcuts);
        Changed?.Invoke();
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
