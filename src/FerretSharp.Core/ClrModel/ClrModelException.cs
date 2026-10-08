namespace FerretSharp.Core.ClrModel;

/// <summary>A failure around the linked project with a message for the user (not built, wrong framework …).</summary>
public sealed class ClrModelException(string kind, string message, string? detail = null) : Exception(message)
{
    public string Kind { get; } = kind;

    public string? Detail { get; } = detail;

    public ModelHostError ToError() => new(Kind, Message, Detail);
}

/// <summary>Error kinds on FerretSharp's side, before or around the ModelHost run.</summary>
public static class ClrModelErrorKind
{
    public const string ProjectNotFound = "ProjectNotFound";
    public const string NotBuilt = "NotBuilt";
    public const string UnsupportedFramework = "UnsupportedFramework";
    public const string NoEfCore = "NoEfCore";
    public const string DotNetMissing = "DotNetMissing";
    public const string HostFailed = "HostFailed";
    public const string Timeout = "Timeout";
}
