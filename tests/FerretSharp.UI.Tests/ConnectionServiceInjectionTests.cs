using System.Reflection;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;
using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Tests;

/// <summary>
/// The services of one connection are scoped to its <see cref="ConnectionScope"/> and reach components as cascading
/// parameters. Injected (<c>@inject</c>/<c>[Inject]</c>) a component gets the WebView scope's instance instead – a
/// "ghost connection" that never connects: so the detail views stayed empty in 3.8, and the filter's column picker never
/// showed C# names until R3a.
/// </summary>
public sealed class ConnectionServiceInjectionTests
{
    private static readonly Type[] ConnectionServices =
    [
        typeof(ConnectionScope), typeof(ActiveConnection), typeof(WorkspaceManager), typeof(ClrModelManager),
        typeof(PresentationService), typeof(LinqConsoleService),
    ];

    [Fact]
    public void Components_never_inject_services_of_a_connection()
    {
        var injected = typeof(ShellState).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null && ConnectionServices.Contains(p.PropertyType))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.Empty(injected);
    }

    [Fact]
    public void The_check_sees_injected_properties()
    {
        // Guards the test above: Razor's @inject must still come out as [Inject] properties.
        var injected = typeof(ShellState).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null);

        Assert.Contains(injected, p => p.PropertyType == typeof(ConnectionHub));
    }
}
