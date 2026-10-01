using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Components;

/// <summary>Small inline SVG icons; they inherit the text color via <c>currentColor</c>.</summary>
internal static class Icons
{
    public static readonly MarkupString Search = new(
        """<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6"><circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3 3"/></svg>""");
}
