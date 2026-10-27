using FerretSharp.Core.Resources;

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
            errors[ConnectionField.Name] = ConnectionText.NameMissing;
        }

        if (string.IsNullOrWhiteSpace(profile.User))
        {
            errors[ConnectionField.User] = ConnectionText.UserMissing;
        }

        switch (profile.Address)
        {
            case HostPortAddress hp:
                if (string.IsNullOrWhiteSpace(hp.Host))
                {
                    errors[ConnectionField.Host] = ConnectionText.HostMissing;
                }
                else if (!OracleAddress.IsPlainHost(hp.Host))
                {
                    errors[ConnectionField.Host] = ConnectionText.HostInvalid;
                }

                if (hp.Port is < 1 or > 65535)
                {
                    errors[ConnectionField.Port] = ConnectionText.PortOutOfRange;
                }

                var hasService = !string.IsNullOrWhiteSpace(hp.ServiceName);
                var hasSid = !string.IsNullOrWhiteSpace(hp.Sid);
                if (hasService == hasSid)
                {
                    errors[ConnectionField.ServiceOrSid] = ConnectionText.ServiceOrSidRequired;
                }
                else if (!OracleAddress.IsPlainName(hasService ? hp.ServiceName! : hp.Sid!))
                {
                    errors[ConnectionField.ServiceOrSid] = ConnectionText.ServiceOrSidInvalid;
                }

                break;

            case TnsAliasAddress tns:
                if (string.IsNullOrWhiteSpace(tns.Alias))
                {
                    errors[ConnectionField.Alias] = ConnectionText.TnsAliasMissing;
                }
                else if (!OracleAddress.IsPlainAlias(tns.Alias))
                {
                    errors[ConnectionField.Alias] = ConnectionText.TnsAliasInvalid;
                }

                break;
        }

        // One field in the dialog for both (WP-64): the error goes there.
        if (profile.ClrProject is { } project && !project.ProjectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || profile.ClrModelFile is { } file && !ClrModel.ModelFile.IsModelFile(file)
            || profile is { ClrProject: not null, ClrModelFile: not null })
        {
            errors[ConnectionField.ClrProject] = ConnectionText.ClrProjectNotCsproj;
        }

        return errors;
    }
}
