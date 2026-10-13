using System.Globalization;

namespace FerretSharp.Core.AppTrace;

/// <summary>
/// One EF Core diagnostic event of a traced app (WP-33), as the source delivers it: the event name and the fields of
/// <see cref="EfEvents.PayloadSpecs"/>, all as text.
/// </summary>
/// <param name="Time">When the app raised the event (its clock, not when it arrived).</param>
public sealed record EfEvent(string Name, IReadOnlyDictionary<string, string> Fields, DateTimeOffset Time)
{
    public string? Field(string key) => Fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>A duration field; EF writes <see cref="TimeSpan"/> with the invariant format (<c>00:00:00.0002734</c>).</summary>
    public TimeSpan? Duration(string key) =>
        Field(key) is { } text && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : null;

    public int? Int(string key) =>
        Field(key) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// The EF Core events a trace reads and the fields it takes from them (WP-33, ADR 0023). The list is fixed here: the
/// source asks the app's <c>DiagnosticSource</c> bridge for exactly these fields and nothing else – never the connection
/// string (ODP.NET strips the password only without <c>Persist Security Info</c>), never parameter values.
/// </summary>
public static class EfEvents
{
    /// <summary>Name of EF Core's <c>DiagnosticListener</c>.</summary>
    public const string Listener = "Microsoft.EntityFrameworkCore";

    public const string CommandExecuted = Listener + ".Database.Command.CommandExecuted";
    public const string CommandError = Listener + ".Database.Command.CommandError";
    public const string DataReaderDisposing = Listener + ".Database.Command.DataReaderDisposing";
    public const string NavigationLazyLoading = Listener + ".Infrastructure.NavigationLazyLoading";

    // Field names as they arrive (the left side of each transform below).
    public const string Text = "Text";
    public const string Source = "Source";
    public const string Method = "Method";
    public const string Result = "Result";
    public const string Duration = "Duration";
    public const string Context = "Context";
    public const string CommandId = "CommandId";
    public const string DataSource = "DataSource";
    public const string Error = "Error";
    public const string ReadCount = "ReadCount";
    public const string Navigation = "Navigation";

    /// <summary>
    /// The <c>FilterAndPayloadSpecs</c> argument of the <c>Microsoft-Diagnostics-DiagnosticSource</c> provider: one line
    /// per event, <c>Listener/Event:-Name=Property.Path;…</c>; the leading <c>-</c> switches off the implicit fields.
    /// <c>Result</c> is the number of affected rows for <c>ExecuteNonQuery</c> (for a reader its type name, for a scalar its
    /// value – only read for non-queries, see <see cref="StatementRecording"/>).
    /// </summary>
    public static readonly string PayloadSpecs = string.Join("\n",
        $"{Listener}/{CommandExecuted}:-{Text}=Command.CommandText;{Source}=CommandSource;{Method}=ExecuteMethod;{Result}=Result;{Duration}=Duration;{Context}=Context.ContextId;{CommandId}=CommandId;{DataSource}=Command.Connection.DataSource",
        $"{Listener}/{CommandError}:-{Text}=Command.CommandText;{Source}=CommandSource;{Duration}=Duration;{Context}=Context.ContextId;{CommandId}=CommandId;{DataSource}=Command.Connection.DataSource;{Error}=Exception.Message",
        $"{Listener}/{DataReaderDisposing}:-{ReadCount}=ReadCount;{Duration}=Duration;{CommandId}=CommandId",
        $"{Listener}/{NavigationLazyLoading}:-{Navigation}=NavigationPropertyName;{Context}=Context.ContextId");
}
