using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// The filter presets of all tables (WP-37), for every connection: a preset is keyed by the table name without owner.
/// Read once; until a read succeeded nothing is written, so a file that was locked or broken at the start keeps the
/// presets it holds. Used from the UI thread only.
/// </summary>
public sealed class FilterPresetService(FilterPresetStore store, ILogger<FilterPresetService> logger)
{
    private IReadOnlyList<FilterPreset> _presets = [];
    private bool _loaded;

    /// <summary>Raised after the presets were read or changed.</summary>
    public event Action? Changed;

    /// <summary>Why the presets could not be read; null if fine. Opening the menu again tries again.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Reads the file unless it was read already; after an error it tries again.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            _presets = await store.LoadAsync(CancellationToken.None);
            _loaded = true;
            LoadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Filter presets could not be read from {File}", store.FilePath);
            LoadError = ex.Message;
        }

        Changed?.Invoke();
    }

    /// <summary>The presets of <paramref name="table"/>, by name.</summary>
    public IReadOnlyList<FilterPreset> For(TableSummary table)
    {
        var key = FilterPresets.TableKey(table);
        return _presets.Where(p => p.Table == key).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The preset of <paramref name="table"/> with this name (ignoring case), if there is one.</summary>
    public FilterPreset? Find(TableSummary table, string name) => For(table).FirstOrDefault(p => FilterPresets.SameName(p.Name, name));

    /// <summary>
    /// Saves a new preset; one of the table with the same name is replaced (the caller asked first). The preset, or the
    /// error to show.
    /// </summary>
    public async Task<(FilterPreset? Preset, string? Error)> SaveAsync(
        TableSummary table, string name, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts)
    {
        if (FilterPresets.NameProblem(name) is { } problem)
        {
            return (null, problem);
        }

        var existing = Find(table, name);
        var preset = new FilterPreset(existing?.Id ?? Guid.NewGuid(), name.Trim(), FilterPresets.TableKey(table), filters, sorts);
        var error = await WriteAsync(_presets.Where(p => p.Id != preset.Id).Append(preset).ToList());
        return error is null ? (preset, null) : (null, error);
    }

    /// <returns>The error to show, or null.</returns>
    public async Task<string?> RenameAsync(FilterPreset preset, string name)
    {
        if (FilterPresets.NameProblem(name) is { } problem)
        {
            return problem;
        }

        if (_presets.Any(p => p.Table == preset.Table && p.Id != preset.Id && FilterPresets.SameName(p.Name, name)))
        {
            return TextFormat.Format(GridText.Presets_NameTaken, name.Trim());
        }

        return await WriteAsync(_presets.Select(p => p.Id == preset.Id ? p with { Name = name.Trim() } : p).ToList());
    }

    /// <returns>The error to show, or null.</returns>
    public Task<string?> DeleteAsync(FilterPreset preset) => WriteAsync(_presets.Where(p => p.Id != preset.Id).ToList());

    private async Task<string?> WriteAsync(IReadOnlyList<FilterPreset> presets)
    {
        if (!_loaded)
        {
            return LoadError ?? GridText.Presets_NotLoaded;
        }

        try
        {
            await store.SaveAsync(presets, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Filter presets could not be saved to {File}", store.FilePath);
            return TextFormat.Format(CommonText.SaveFailed, ex.Message);
        }

        _presets = presets;
        Changed?.Invoke();
        return null;
    }
}
