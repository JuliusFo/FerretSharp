using System.Reflection;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The texts of the ModelHost (WP-29): it is a separate .NET 8 program, so the resource checks of Core.Tests and UI.Tests do
/// not see it. Needs no database – only the built host.
/// </summary>
public sealed class ModelHostResourceTests
{
    [Fact]
    public void Every_text_exists_in_English_and_German_with_the_same_placeholders()
    {
        // Only the text class is touched: the rest of the host references Roslyn and EF Core, which this process need not load.
        var texts = Assembly.LoadFrom(ModelHostTests.ModelHostPath).GetType("FerretSharp.ModelHost.Resources.ModelHostText", throwOnError: true)!;

        Assert.Empty(ResourceChecks.Problems(texts));
    }
}
