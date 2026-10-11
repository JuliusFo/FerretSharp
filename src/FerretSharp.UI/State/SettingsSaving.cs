using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Saving a setting from the UI (R4b: one helper instead of a copy in the shell, the settings pages and the change overview).</summary>
public static class SettingsSaving
{
    /// <summary>
    /// Saves a setting that is in effect already – only writing the file may fail (or the session does not save, R4a):
    /// then a notice says so, and the setting stays for this session.
    /// </summary>
    /// <param name="logger">Logs the failure too, if given.</param>
    /// <returns>False if it was not saved.</returns>
    public static async Task<bool> TrySaveSettingsAsync(this ShellState shell, Func<Task> save, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(save);
        try
        {
            await save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Setting not saved");
            shell.Notify(TextFormat.Format(ShellText.Settings_NotSaved, ex.Message));
            return false;
        }
    }
}
