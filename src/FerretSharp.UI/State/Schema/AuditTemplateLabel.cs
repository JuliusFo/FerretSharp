using FerretSharp.Core.Audit;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>Which audit template applies and where it is set (WP-51), for the audit line's tooltip and the connection dialog.</summary>
public static class AuditTemplateLabel
{
    public static string Of(AuditTemplateLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        return load.Origin switch
        {
            AuditTemplateOrigin.Connection => TextFormat.Format(ShellText.AuditTemplate_FromConnection, load.Path),
            AuditTemplateOrigin.Group => TextFormat.Format(ShellText.AuditTemplate_FromGroup, load.Group, load.Path),
            AuditTemplateOrigin.Global => TextFormat.Format(ShellText.AuditTemplate_FromSettings, load.Path),
            _ => ShellText.AuditTemplate_BuiltIn,
        };
    }
}
