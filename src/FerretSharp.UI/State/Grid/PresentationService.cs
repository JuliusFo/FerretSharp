using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// The presentation of tables for the components (WP-12): the loaded C# model and the "C#-Namen" setting in one place.
/// <see cref="Changed"/> fires only when either of them really changes – not for every progress step of a model load,
/// and not for a reload after a build that changed nothing a table shows. Which tables a change touches is known
/// (<see cref="ChangedSince"/>), so after a build only the views of those tables render again (ADR 0016). The header lines
/// (type and NOT NULL) change apart from it (<see cref="HeaderChanged"/>): the values read as before, nothing is fetched again.
/// </summary>
public sealed class PresentationService : IDisposable
{
    /// <summary>Changes remembered for <see cref="ChangedSince"/>; a component further behind renders anyway.</summary>
    private const int KeptChanges = 16;

    private readonly ClrModelManager _models;
    private readonly AppSettingsService _settings;
    private readonly ActiveConnection? _active;
    private readonly Lock _lock = new();
    private readonly List<(int Version, IReadOnlySet<TableRef>? Tables, bool Names)> _changes = [];
    private ClrModelMapping? _mapping;
    private ClrNameDisplay _names;
    private ColumnHeaderLines _header;
    private ModelTableSignatures _signatures;

    /// <param name="active">The connection, for its audit tables (WP-63); none in tests that do not need them.</param>
    public PresentationService(ClrModelManager models, AppSettingsService settings, ActiveConnection? active = null)
    {
        _models = models;
        _settings = settings;
        _active = active;
        _mapping = models.Mapping;
        _names = settings.Current.ClrNames;
        _header = ColumnHeaderLines.Of(settings.Current);
        _signatures = ModelTableSignatures.Of(_mapping);
        _models.Changed += OnChanged;
        _settings.Changed += OnChanged;
    }

    /// <summary>The model or the setting changed; may fire on a background thread.</summary>
    public event Action? Changed;

    public ClrModelMapping? Mapping => _mapping;

    public ClrNameDisplay Names => _names;

    /// <summary>Type and NOT NULL in the column headers (table grid and SQL result) as the settings show them.</summary>
    public ColumnHeaderLines Header
    {
        get
        {
            lock (_lock)
            {
                return _header;
            }
        }
    }

    /// <summary><see cref="Header"/> changed; may fire on a background thread. Not a <see cref="Changed"/>: rows read as before.</summary>
    public event Action? HeaderChanged;

    /// <summary>Changes whenever <see cref="Changed"/> fires: lets a component tell whether its presentation is stale.</summary>
    public int Version { get; private set; }

    /// <summary>The table's presentation; an audit table's columns named by its base table's entity (WP-63).</summary>
    public TablePresentation For(TableDetails details)
    {
        var model = ModelTableOf(details.Table.Ref);
        return TablePresentation.Create(details, _mapping, _names, model == details.Table.Ref ? null : model);
    }

    /// <summary>The table whose entity names the columns of <paramref name="table"/>: an audit table's base table (WP-63), else itself.</summary>
    public TableRef ModelTableOf(TableRef table) => _active?.Schema?.AuditBaseOf(table) ?? table;

    /// <summary>The entity of a table by its short name (<c>Kunde</c>); null without one.</summary>
    public string? EntityNameOf(TableRef table) =>
        _mapping?.EntityOf(table) is { } entity ? ClrModelMapping.ShortName(entity.Entity.ClrType) : null;

    /// <summary>The entity's name if C# names are shown at all (explorer, tab tooltip).</summary>
    public string? ShownEntityNameOf(TableRef table) => _names == ClrNameDisplay.Off ? null : EntityNameOf(table);

    /// <summary>Whether the presentation of <paramref name="table"/> changed after <paramref name="version"/>.</summary>
    /// <remarks>An audit table changes with its base table: its columns are named by the base entity (WP-63).</remarks>
    public bool ChangedSince(int version, TableRef table)
    {
        var model = ModelTableOf(table);
        return ChangedSince(version, c => c.Tables is null || c.Tables.Contains(table) || c.Tables.Contains(model));
    }

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
        var header = ColumnHeaderLines.Of(_settings.Current);
        bool headerChanged;
        lock (_lock)
        {
            headerChanged = header != _header;
            _header = header;
        }

        if (headerChanged)
        {
            HeaderChanged?.Invoke();
        }

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
