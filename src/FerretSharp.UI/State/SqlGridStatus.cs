using FerretSharp.Core.Connections;

namespace FerretSharp.UI.State;

/// <summary>State of a result grid (LINQ console) for the footer below it.</summary>
/// <param name="AllLoaded">The last page has arrived.</param>
/// <param name="DataAsOf">Start of the read-only snapshot (locked workspaces).</param>
public sealed record SqlGridStatus(bool Loading, int RowsLoaded, bool AllLoaded, TimeSpan? Elapsed, DateTimeOffset? DataAsOf, DatabaseException? Error);
