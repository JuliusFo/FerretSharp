namespace FerretSharp.UI.State;

/// <summary>An entry of <c>MoreMenu</c> (the "⋯" button of a page or group head).</summary>
public sealed record MoreMenuItem(string Label, Action Run, bool Enabled = true);
