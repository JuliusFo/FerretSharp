namespace FerretSharp.UI.State;

/// <summary>
/// The <c>ferretsharp://</c> protocol for the current Windows user (WP-37, ADR 0026): which FerretSharp opens links. The
/// installer registers and removes it; a portable copy or a build does so from the settings. Implemented by the host.
/// </summary>
public interface IProtocolRegistration
{
    /// <summary>The program links start now (from the registration); null if none is registered.</summary>
    string? RegisteredProgram { get; }

    /// <summary>This FerretSharp's program file.</summary>
    string OwnProgram { get; }

    /// <summary>Links open with this FerretSharp from now on (replaces another copy's registration).</summary>
    void Register();

    /// <summary>Removes the registration if it is this FerretSharp's; another copy's stays.</summary>
    void Unregister();
}
