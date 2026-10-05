using System.Data;
using System.Data.Common;
using System.Globalization;
using FerretSharp.Core.ClrModel;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FerretSharp.ModelHost;

/// <summary>
/// Keeps EF away from the database (ADR 0011): opening the connection and every command are suppressed, the commands are
/// recorded, and EF gets an empty result. One instance for the console's lifetime – new interceptor instances per context
/// would make EF build a new internal service provider each time – recording between <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
internal sealed class CommandCapture : DbCommandInterceptor
{
    private List<CapturedCommand>? _commands;

    public ConnectionSuppressor Connections { get; } = new();

    public void Start() => _commands = [];

    public IReadOnlyList<CapturedCommand> Stop()
    {
        var commands = _commands ?? [];
        _commands = null;
        return commands;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command, LinqProtocol.Reader);
        return InterceptionResult<DbDataReader>.SuppressWithResult(Empty());
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command, LinqProtocol.Reader);
        return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(Empty()));
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command, LinqProtocol.NonQuery);
        return InterceptionResult<int>.SuppressWithResult(0);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command, LinqProtocol.NonQuery);
        return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command, LinqProtocol.Scalar);
        return InterceptionResult<object>.SuppressWithResult(DBNull.Value);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command, LinqProtocol.Scalar);
        return ValueTask.FromResult(InterceptionResult<object>.SuppressWithResult(DBNull.Value));
    }

    private static DbDataReader Empty() => new DataTable().CreateDataReader();

    private void Record(DbCommand command, string kind) =>
        _commands?.Add(new CapturedCommand(kind, command.CommandText, command.Parameters.Cast<DbParameter>().Select(Parameter).ToList()));

    private static CapturedParameter Parameter(DbParameter parameter)
    {
        var value = parameter.Value is DBNull ? null : parameter.Value;
        // The Oracle provider's parameter type, read by reflection: FerretSharp compiles against no provider version.
        var oracleType = parameter.GetType().GetProperty("OracleDbType")?.GetValue(parameter)?.ToString();
        return new CapturedParameter(parameter.ParameterName.TrimStart(':'), oracleType, value?.GetType().Name, Text(value));
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>"Opens" and "closes" the connection without touching it.</summary>
    internal sealed class ConnectionSuppressor : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }
}
