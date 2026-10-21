namespace FerretSharp.Core.Settings;

/// <summary>
/// Where the project lives on GitHub: updates, release notes and their images, the redirect page for links (R6b, review
/// 3.40.0, B11: owner and repository were written in four places). The account is never renamed or given up – its name
/// is part of every link and of the update channel (ADR 0026, addendum R6a).
/// </summary>
public static class ProjectSite
{
    public const string Owner = "JuliusFo";

    public const string Repository = "FerretSharp";

    public const string Url = "https://github.com/" + Owner + "/" + Repository;

    public const string Releases = Url + "/releases";

    /// <summary>The path of the repository's files below <c>raw.githubusercontent.com</c>.</summary>
    public const string RawPath = "/" + Owner + "/" + Repository + "/";

    /// <summary>GitHub Pages of the repository; the host is the owner in lower case (a constant cannot lower it).</summary>
    public const string Pages = "https://juliusfo.github.io/" + Repository + "/";
}
