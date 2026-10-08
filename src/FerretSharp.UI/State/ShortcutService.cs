using FerretSharp.Core.Settings;
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
        _map = new ShortcutMap(settings.Current.Shortcuts);
        settings.Changed += OnSettingsChanged;
    }

    public ShortcutMap Map => _map;

    /// <summary>A shortcut changed (on the thread that changed the settings).</summary>
    public event Action? Changed;

    /// <summary>For tooltips and messages, e.g. <c>Ctrl+S</c>; null if the action has no shortcut.</summary>
    public string? Label(ShortcutAction action) => _map.Chord(action)?.Label;

    /// <summary>For a tooltip after the text: <c> (Ctrl+S)</c>, or nothing if the action has no shortcut.</summary>
    public string Hint(ShortcutAction action) => Label(action) is { } label ? $" ({label})" : "";

    /// <summary>For a sentence: <c> mit Ctrl+Enter</c>, or nothing if the action has no shortcut.</summary>
    public string With(ShortcutAction action) => Label(action) is { } label ? $" mit {label}" : "";

    /// <summary>Whether a key event (Blazor) is the action's shortcut.</summary>
    public bool Is(ShortcutAction action, KeyboardEventArgs e) =>
        _map.Chord(action) is { } chord && chord == KeyChord.FromEvent(e.Key, e.CtrlKey, e.ShiftKey, e.AltKey);

    /// <summary>The local combos for <c>shortcuts.js</c> (<c>setLocal</c>): those grid.js handles itself.</summary>
    public Dictionary<string, string?> LocalCombos => new() { ["form"] = _map.Chord(ShortcutAction.ToggleForm)?.Text };

    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task SetAsync(ShortcutAction action, KeyChord? chord) => UpdateAsync(map => map.With(action, chord));

    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task ResetAsync(ShortcutAction action) => UpdateAsync(map => map.Without(action));

    /// <exception cref="IOException">Not saved; the change stays in effect for this session.</exception>
    public Task ResetAllAsync() => UpdateAsync(map => map.Reset());

    private Task UpdateAsync(Func<ShortcutMap, ShortcutOverrides> change) =>
        _settings.UpdateAsync(s => s with { Shortcuts = change(new ShortcutMap(s.Shortcuts)) }, CancellationToken.None);

    private void OnSettingsChanged()
    {
        var overrides = _settings.Current.Shortcuts;
        if (overrides.Equals(_map.Overrides))
        {
            return;
        }

        _map = new ShortcutMap(overrides);
        Changed?.Invoke();
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
