using System.IO;
using FerretSharp.Core.Links;
using FerretSharp.UI.State;
using Microsoft.Win32;

namespace FerretSharp.App.Services;

/// <summary>
/// The <c>ferretsharp://</c> protocol under <c>HKCU\Software\Classes</c> (WP-37, ADR 0026): for the current user only, no
/// administrator rights. Windows starts <c>"FerretSharp.exe" "%1"</c> with the link; a running instance gets it through
/// <see cref="LinkPipe"/>. Written by the installer's hooks (<see cref="Program"/>) and from the settings.
/// </summary>
internal sealed class ProtocolRegistration : IProtocolRegistration
{
    private const string KeyPath = @"Software\Classes\" + TableLink.Scheme;
    private const string CommandPath = KeyPath + @"\shell\open\command";

    public string OwnProgram { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FerretSharp.exe");

    public string? RegisteredProgram
    {
        get
        {
            using var command = Registry.CurrentUser.OpenSubKey(CommandPath);
            return ProtocolCommand.ProgramOf(command?.GetValue(null) as string);
        }
    }

    public void Register() => Register(OwnProgram);

    public void Unregister() => Unregister(OwnProgram);

    /// <summary>Links open with <paramref name="program"/>; what the key held before is replaced.</summary>
    public static void Register(string program)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(null, "URL:FerretSharp");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue(null, $"\"{program}\",0");
        }

        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(null, ProtocolCommand.For(program));
    }

    /// <summary>Removes the protocol if it starts <paramref name="program"/> (an uninstall leaves another copy's registration).</summary>
    public static void Unregister(string program)
    {
        string? registered;
        using (var command = Registry.CurrentUser.OpenSubKey(CommandPath))
        {
            registered = ProtocolCommand.ProgramOf(command?.GetValue(null) as string);
        }

        if (ProtocolCommand.SameProgram(registered, program))
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
    }
}
