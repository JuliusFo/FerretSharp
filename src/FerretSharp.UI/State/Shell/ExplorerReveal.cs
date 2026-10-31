using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// "Show in explorer" (WP-70, <see cref="ShellState.RevealInExplorer"/>): the explorer of the connection shows the table or
/// PL/SQL unit – its section, no search – and marks it. Taken once per <paramref name="Id"/>.
/// </summary>
public sealed record ExplorerReveal(int Id, Guid ConnectionId, TableRef? Table, PlSqlRef? Unit);
