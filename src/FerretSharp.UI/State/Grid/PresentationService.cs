using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// The presentation of tables for the components (WP-12): the loaded C# model and the "C#-Namen" setting in one place.
/// <see cref="Changed"/> fires only when either of them really changes – not for every progress step of a model load,
/// and not for a reload after a build that changed nothing a table shows. Which tables a change touches is known
/// (<see cref="ChangedSince"/>), so after a build only the views of those tables render again (ADR 0016).
/// </summary>
public sealed class PresentationService : IDisposable
{
    /// <summary>Changes remembered for <see cref="ChangedSince"/>; a component further behind renders anyway.</summary>
    private const int KeptChanges = 16;

    private readonly ClrModelManager _models;
    private readonly AppSettingsService _settings;
    private readonly Lock _lock = new();
    private readonly List<(int Version, IReadOnlySet<TableRef>? Tables, bool Names)> _changes = [];
    private ClrModelMapping? _mapping;
    private ClrNameDisplay _names;
    private ModelTableSignatures _signatures;

    public PresentationService(ClrModelManager models, AppSettingsService settings)
    {
        _models = models;
        _settings = settings;
        _mapping = models.Mapping;
        _names = settings.Current.ClrNames;
        _signatures = ModelTableSignatures.Of(_mapping);
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

    /// <summary>Whether the presentation of <paramref name="table"/> changed after <paramref name="version"/>.</summary>
    public bool ChangedSince(int version, TableRef table) => ChangedSince(version, c => c.Tables is null || c.Tables.Contains(table));

    /// <summary>Whether the entity names shown for tables (or the setting) changed after <paramref name="version"/>.</summary>
    public bool NamesChangedSince(int version) => ChangedSince(version, c => c.Names);

    private bool ChangedSince(int version, Func<(int Version, IReadOnlySet<TableRef>? Tables, bool Names), bool> touches)
    {
        lock (_lock)
        {
            if (version >= Version)
            {
                return false;
            }

            if (_changes.Count == 0 || _changes[0].Version > version + 1)
            {
                return true; // further behind than remembered
            }

            return _changes.Any(c => c.Version > version && touches(c));
        }
    }

    public void Dispose()
    {
        _models.Changed -= OnChanged;
        _settings.Changed -= OnChanged;
    }

    /// <summary>On the thread that reported (a model load runs in the background): comparing costs nothing on the UI thread.</summary>
    private void OnChanged()
    {
        var mapping = _models.Mapping;
        var names = _settings.Current.ClrNames;
        ClrModelMapping? shown;
        ModelTableSignatures shownSignatures;
        lock (_lock)
        {
            if (ReferenceEquals(mapping, _mapping) && names == _names)
            {
                return;
            }

            (shown, shownSignatures) = (_mapping, _signatures);
        }

        // Outside the lock: components ask ChangedSince on the UI thread meanwhile.
        var signatures = ReferenceEquals(mapping, shown) ? shownSignatures : ModelTableSignatures.Of(mapping);
        lock (_lock)
        {
            if (ReferenceEquals(mapping, _mapping) && names == _names)
            {
                return; // another thread got there first
            }

            var tables = names != _names ? null : _signatures.ChangedTables(signatures);
            var namesChanged = names != _names || !_signatures.SameEntityNames(signatures);
            (_mapping, _names, _signatures) = (mapping, names, signatures);
            if (tables is { Count: 0 } && !namesChanged)
            {
                return; // a new build that changed nothing a table shows: keep the views as they are
            }

            Version++;
            _changes.Add((Version, tables, namesChanged));
            if (_changes.Count > KeptChanges)
            {
                _changes.RemoveAt(0);
            }
        }

        Changed?.Invoke();
    }
}
