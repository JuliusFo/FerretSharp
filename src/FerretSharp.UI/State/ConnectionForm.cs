using System.Globalization;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

public enum AddressMode { HostPort, TnsAlias }

/// <summary>Mutable edit model behind the connection dialog; converts to and from <see cref="ConnectionProfile"/>.</summary>
public sealed class ConnectionForm
{
    private ConnectionKind _kind = ConnectionKind.Dev;

    public Guid Id { get; private init; } = Guid.NewGuid();

    public bool IsNew { get; private init; } = true;

    public string Name { get; set; } = "";

    public string Group { get; set; } = "";

    /// <summary>Profile whose stored password is used when <see cref="Password"/> is empty (edit: itself, duplicate: the original).</summary>
    public Guid? PasswordSourceId { get; private init; }

    public ConnectionKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            if (!ReadOnlyTouched)
            {
                ReadOnly = ConnectionProfile.IsReadOnlyByDefault(value);
            }
        }
    }

    public AddressMode Mode { get; set; } = AddressMode.HostPort;

    public string Host { get; set; } = "";

    public string Port { get; set; } = HostPortAddress.DefaultPort.ToString(CultureInfo.InvariantCulture);

    public bool UseSid { get; set; }

    public string ServiceName { get; set; } = "";

    public string Sid { get; set; } = "";

    public string Alias { get; set; } = "";

    public string TnsAdminPath { get; set; } = "";

    public string User { get; set; } = "";

    /// <summary>Empty when editing means "keep the stored password".</summary>
    public string Password { get; set; } = "";

    public string DefaultSchema { get; set; } = "";

    public bool ReadOnly { get; private set; }

    /// <summary>The .csproj with the DbContext (C# model, WP-11); empty = none.</summary>
    public string ClrProjectFile { get; set; } = "";

    public string ClrConfiguration { get; set; } = "Debug";

    /// <summary>DbContext type if the project has several; empty = its only one.</summary>
    public string ClrContextType { get; set; } = "";

    /// <summary>Once the user toggled read-only, changing the kind no longer overrides it.</summary>
    public bool ReadOnlyTouched { get; private set; }

    public void SetReadOnly(bool value)
    {
        ReadOnly = value;
        ReadOnlyTouched = true;
    }

    /// <summary>Edit an existing connection.</summary>
    public static ConnectionForm FromProfile(ConnectionProfile profile) =>
        Create(profile, profile.Id, isNew: false, profile.Name);

    /// <summary>A new connection prefilled from <paramref name="original"/>, reusing its stored password unless one is entered.</summary>
    public static ConnectionForm Duplicate(ConnectionProfile original) =>
        Create(original, Guid.NewGuid(), isNew: true, TextFormat.Format(ShellText.ConnectionForm_CopyName, original.Name));

    private static ConnectionForm Create(ConnectionProfile profile, Guid id, bool isNew, string name)
    {
        var form = new ConnectionForm
        {
            Id = id,
            IsNew = isNew,
            PasswordSourceId = profile.Id,
            Name = name,
            Group = profile.Group ?? "",
            User = profile.User,
            DefaultSchema = profile.DefaultSchema ?? "",
            ReadOnlyTouched = true,
            ClrProjectFile = profile.ClrProject?.ProjectFile ?? "",
            ClrConfiguration = profile.ClrProject?.Configuration ?? "Debug",
            ClrContextType = profile.ClrProject?.ContextType ?? "",
        };
        form._kind = profile.Kind;
        form.ReadOnly = profile.ReadOnly;

        switch (profile.Address)
        {
            case HostPortAddress hp:
                form.Mode = AddressMode.HostPort;
                form.Host = hp.Host;
                form.Port = hp.Port.ToString(CultureInfo.InvariantCulture);
                form.UseSid = string.IsNullOrEmpty(hp.ServiceName) && !string.IsNullOrEmpty(hp.Sid);
                form.ServiceName = hp.ServiceName ?? "";
                form.Sid = hp.Sid ?? "";
                break;
            case TnsAliasAddress tns:
                form.Mode = AddressMode.TnsAlias;
                form.Alias = tns.Alias;
                form.TnsAdminPath = tns.TnsAdminPath ?? "";
                break;
        }

        return form;
    }

    /// <summary>Builds the profile and validates it; the port is checked here because the profile holds an int.</summary>
    public (ConnectionProfile Profile, IReadOnlyDictionary<string, string> Errors) ToProfile()
    {
        var errors = new Dictionary<string, string>();
        OracleAddress address;

        if (Mode == AddressMode.HostPort)
        {
            if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                port = 0;
            }

            address = new HostPortAddress(
                Host.Trim(),
                port,
                UseSid ? null : NullIfBlank(ServiceName),
                UseSid ? NullIfBlank(Sid) : null);
        }
        else
        {
            address = new TnsAliasAddress(Alias.Trim(), NullIfBlank(TnsAdminPath));
        }

        var clrProject = NullIfBlank(ClrProjectFile.Trim('"')) is { } projectFile
            ? new ClrProjectLink(projectFile, NullIfBlank(ClrConfiguration) ?? "Debug", NullIfBlank(ClrContextType))
            : null;
        var profile = new ConnectionProfile(Id, Name.Trim(), Kind, address, User.Trim(), NullIfBlank(DefaultSchema), ReadOnly, NullIfBlank(Group), clrProject);
        foreach (var (field, message) in ConnectionProfileValidator.Validate(profile))
        {
            errors[field] = message;
        }

        return (profile, errors);
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
