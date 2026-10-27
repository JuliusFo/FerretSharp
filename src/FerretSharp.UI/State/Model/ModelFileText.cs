using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>How the origin of a linked model file (WP-64) reads on the model page and in the status bar.</summary>
public static class ModelFileText
{
    /// <summary>"Shop.Data (Release, ShopContext) · release @ a1b2c3d" – project, configuration, context, Git.</summary>
    public static string Source(ModelFileOrigin origin)
    {
        var build = origin.ContextType is { Length: > 0 } context ? $"{origin.Configuration}, {context}" : origin.Configuration;
        var text = TextFormat.Format(ModelViewText.Model_FileSource, origin.Project, build);
        return Git(origin) is { } git ? $"{text} · {git}" : text;
    }

    /// <summary>"12.10.2026 · release @ a1b2c3d": when exported and from which branch, for the status bar's tooltip.</summary>
    public static string Short(ModelFileOrigin origin)
    {
        var date = origin.ExportedAt.ToLocalTime().ToString("dd.MM.yyyy", System.Globalization.CultureInfo.CurrentCulture);
        return Git(origin) is { } git ? $"{date} · {git}" : date;
    }

    /// <summary>"release @ a1b2c3d", only the branch or only the commit; null outside a Git working copy.</summary>
    private static string? Git(ModelFileOrigin origin) => (origin.Branch, origin.Commit) switch
    {
        ({ } branch, { } commit) when branch != commit => $"{branch} @ {commit}",
        ({ } branch, _) => branch,
        (_, { } commit) => commit,
        _ => null,
    };
}
