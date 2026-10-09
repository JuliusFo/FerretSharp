using FerretSharp.Core.Connections;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

public static class ConnectionKindInfo
{
    public static IReadOnlyList<ConnectionKind> All { get; } = [ConnectionKind.Dev, ConnectionKind.Test, ConnectionKind.Prod, ConnectionKind.Other];

    public static string Label(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dev => ShellText.ConnectionKind_Dev,
        ConnectionKind.Test => ShellText.ConnectionKind_Test,
        ConnectionKind.Prod => ShellText.ConnectionKind_Prod,
        _ => ShellText.ConnectionKind_Other,
    };

    public static string Short(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dev => "DEV",
        ConnectionKind.Test => "TEST",
        ConnectionKind.Prod => "PROD",
        _ => "",
    };

    /// <summary>CSS class that colors <c>.dot</c> children (see ferretsharp.css).</summary>
    public static string CssClass(ConnectionKind kind) => "kind-" + kind.ToString().ToLowerInvariant();
}
