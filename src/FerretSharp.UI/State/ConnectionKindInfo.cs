using FerretSharp.Core.Connections;

namespace FerretSharp.UI.State;

public static class ConnectionKindInfo
{
    public static IReadOnlyList<ConnectionKind> All { get; } = [ConnectionKind.Dev, ConnectionKind.Test, ConnectionKind.Prod, ConnectionKind.Other];

    public static string Label(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Dev => "Entwicklung",
        ConnectionKind.Test => "Test",
        ConnectionKind.Prod => "Produktion",
        _ => "Sonstige",
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
