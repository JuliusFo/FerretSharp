namespace FerretSharp.Core.Settings;

public enum ThemeMode
{
    /// <summary>Follow the Windows setting (default).</summary>
    System,
    Light,
    Dark,
}

/// <summary>User preferences of the app (not per connection). Saved as <c>settings.json</c> in the data directory.</summary>
public sealed record AppSettings
{
    public static readonly AppSettings Default = new();

    public ThemeMode Theme { get; init; } = ThemeMode.System;
}
