using System.Data.Common;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Oracle;

/// <summary>Reads whose rows are handed on while the cursor is open (WP-54, ADR 0029; review 3.44.0, B2: out of the main file).</summary>
public sealed partial class OracleSession
{
    /// <summary>
    /// A query whose rows are handed on while it reads (the export of all rows, WP-54). Like
    /// <see cref="ExecuteReaderAsync{T}(string, IReadOnlyList{QueryParameter}, Func{DbDataReader, CancellationToken, Task{T}}, int, CancellationToken)"/>
    /// it is repeated in a new snapshot when the snapshot cannot serve it – but only until <paramref name="read"/> has passed
    /// rows on (<see cref="NestedReader.HandingOn"/>): those cannot be taken back, so an unusable snapshot after that
    /// (ORA-01555 after a long read) is reported. Meanwhile <paramref name="read"/> may run further queries on the same
    /// connection through the <see cref="NestedReader"/> it gets – the session's gate is held by this call.
    /// </summary>
    /// <exception cref="InvalidOperationException">The statement is not a plain query (see <see cref="IsReadOnlyStatement"/>).</exception>
    internal async Task<T> ExecuteStreamingReaderAsync<T>(
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        Func<DbDataReader, NestedReader, CancellationToken, Task<T>> read,
        int lobPrefetch,
        CancellationToken cancellationToken)
    {
        GuardReadOnly(sql);
        return await ReadInSnapshotAsync(
            nested => ReadCoreAsync(sql, parameters, (reader, ct) => read(reader, nested, ct), cancellationToken, lobPrefetch), cancellationToken);
    }

    /// <summary>
    /// Queries while the reader of <see cref="ExecuteStreamingReaderAsync{T}"/> is open (display names of an export, WP-54):
    /// on the same connection, inside the gate that call holds – Oracle keeps several cursors of a session open. Valid only
    /// while that call reads.
    /// </summary>
    internal sealed class NestedReader(OracleSession session)
    {
        private bool _ended;

        /// <summary>The caller has passed rows on: from now on the query is not repeated in a new snapshot.</summary>
        public bool HandedOn { get; private set; }

        /// <summary>Called right before the caller passes rows on (<see cref="HandedOn"/>).</summary>
        public void HandingOn() => HandedOn = true;

        /// <exception cref="DatabaseException">The query failed (translated here: the gate translates only what leaves it).</exception>
        public async Task<T> ReadAsync<T>(
            string sql, IReadOnlyList<QueryParameter> parameters, Func<DbDataReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_ended, this);
            GuardReadOnly(sql);

            var outer = session._running;
            try
            {
                return await session.ReadCoreAsync(sql, parameters, read, cancellationToken);
            }
            catch (Exception ex) when (session.Translate(ex) is { } translated)
            {
                throw translated;
            }
            finally
            {
                session._running = outer; // disposing the session cancels the streaming command again
            }
        }

        internal void End() => _ended = true;
    }
}
