using System.Globalization;
using System.Runtime.CompilerServices;

namespace FerretSharp.Testing;

/// <summary>
/// The UI language of the tests (WP-29): German, because the tests written before WP-29 check the German texts – on every
/// machine, also on the English CI runners. A test of the English texts switches with <see cref="Use"/>.
/// </summary>
internal static class UiCulture
{
    [ModuleInitializer]
    internal static void UseGermanByDefault() =>
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");

    /// <summary>Switches the UI culture of the current test until disposed (it flows with the test's async context only).</summary>
    public static IDisposable Use(string name)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        return new Restore(previous);
    }

    private sealed class Restore(CultureInfo previous) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }
}
