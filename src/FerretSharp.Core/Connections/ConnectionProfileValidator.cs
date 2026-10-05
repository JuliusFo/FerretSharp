namespace FerretSharp.Core.Connections;

/// <summary>Field keys used in validation results, so the UI can show errors next to the right input.</summary>
public static class ConnectionField
{
    public const string Name = nameof(Name);
    public const string Host = nameof(Host);
    public const string Port = nameof(Port);
    public const string ServiceOrSid = nameof(ServiceOrSid);
    public const string Alias = nameof(Alias);
    public const string User = nameof(User);
    public const string ClrProject = nameof(ClrProject);
}

public static class ConnectionProfileValidator
{
    /// <returns>Field key → message; empty if the profile is valid.</returns>
    public static IReadOnlyDictionary<string, string> Validate(ConnectionProfile profile)
    {
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors[ConnectionField.Name] = "Name fehlt.";
        }

        if (string.IsNullOrWhiteSpace(profile.User))
        {
            errors[ConnectionField.User] = "Benutzer fehlt.";
        }

        switch (profile.Address)
        {
            case HostPortAddress hp:
                if (string.IsNullOrWhiteSpace(hp.Host))
                {
                    errors[ConnectionField.Host] = "Host fehlt.";
                }

                if (hp.Port is < 1 or > 65535)
                {
                    errors[ConnectionField.Port] = "Port muss zwischen 1 und 65535 liegen.";
                }

                var hasService = !string.IsNullOrWhiteSpace(hp.ServiceName);
                var hasSid = !string.IsNullOrWhiteSpace(hp.Sid);
                if (hasService == hasSid)
                {
                    errors[ConnectionField.ServiceOrSid] = "Entweder Service Name oder SID angeben.";
                }

                break;

            case TnsAliasAddress tns:
                if (string.IsNullOrWhiteSpace(tns.Alias))
                {
                    errors[ConnectionField.Alias] = "TNS-Alias fehlt.";
                }

                break;
        }

        if (profile.ClrProject is { } project && !project.ProjectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            errors[ConnectionField.ClrProject] = "Bitte die .csproj des Projekts mit dem DbContext angeben.";
        }

        return errors;
    }
}
