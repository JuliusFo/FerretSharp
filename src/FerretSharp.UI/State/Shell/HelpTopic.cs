namespace FerretSharp.UI.State;

/// <summary>
/// A topic of the help beside a view (WP-89, ADR 0051). The help explains where it is needed, so each topic opens beside
/// the view it is about; further topics (filters, audit templates, shortcuts) are in the backlog.
/// </summary>
public enum HelpTopic
{
    /// <summary>SQL scripts: folders, the header, the cell menu, saving – beside the SQL editor.</summary>
    Scripts,
}
