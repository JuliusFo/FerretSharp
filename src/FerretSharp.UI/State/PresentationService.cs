using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// The presentation of tables for the components (WP-12): the loaded C# model and the "C#-Namen" setting in one place.
/// <see cref="Changed"/> fires only when either of them really changes – not for every progress step of a model load –
/// so the grid can relabel its columns without reloading on every event.
/// </summary>
public sealed class PresentationService : IDisposable
{
    private readonly ClrModelManager _models;
    private readonly AppSettingsService _settings;
    private ClrModelMapping? _mapping;
    private ClrNameDisplay _names;

    public PresentationService(ClrModelManager models, AppSettingsService settings)
    {
        _models = models;
        _settings = settings;
        _mapping = models.Mapping;
        _names = settings.Current.ClrNames;
        _models.Changed += OnChanged;
        _settings.Changed += OnChanged;
    }

    /// <summary>The model or the setting changed; may fire on a background thread.</summary>
    public event Action? Changed;

    public ClrModelMapping? Mapping => _mapping;

    public ClrNameDisplay Names => _names;

    /// <summary>Changes whenever <see cref="Changed"/> fires: lets a component tell whether its presentation is stale.</summary>
    public int Version { get; private set; }

    public TablePresentation For(TableDetails details) => TablePresentation.Create(details, _mapping, _names);

    /// <summary>The entity of a table by its short name (<c>Kunde</c>); null without one.</summary>
    public string? EntityNameOf(TableRef table) =>
        _mapping?.EntityOf(table) is { } entity ? ClrModelMapping.ShortName(entity.Entity.ClrType) : null;

    /// <summary>The entity's name if C# names are shown at all (explorer, tab tooltip).</summary>
    public string? ShownEntityNameOf(TableRef table) => _names == ClrNameDisplay.Off ? null : EntityNameOf(table);

    public void Dispose()
    {
        _models.Changed -= OnChanged;
        _settings.Changed -= OnChanged;
    }

    private void OnChanged()
    {
        var mapping = _models.Mapping;
        var names = _settings.Current.ClrNames;
        if (ReferenceEquals(mapping, _mapping) && names == _names)
        {
            return;
        }

        _mapping = mapping;
        _names = names;
        Version++;
        Changed?.Invoke();
    }
}
