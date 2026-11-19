namespace FerretSharp.UI.State;

/// <summary>
/// A topic of the help (WP-89, ADR 0051), shown in the dock on the right since WP-92 (ADR 0052) – beside whatever view is
/// active; further topics (filters, audit templates, shortcuts) are in the backlog.
/// </summary>
public enum HelpTopic
{
    /// <summary>SQL scripts: folders, the header, the cell menu, saving.</summary>
    Scripts,
}
