using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Oracle;

public class OracleSessionAttributeTests
{
    [Theory]
    [InlineData("Bug 3711", "Bug 3711")]
    [InlineData("Prüfung – Änderung", "Pruefung - Aenderung")]
    [InlineData("Straße, Öl, Übung", "Strasse, Oel, Uebung")]
    [InlineData("Café Señor", "Cafe Senor")]
    [InlineData("Ticket 🐛", "Ticket ?")]
    [InlineData("Zeile1\nZeile2", "Zeile1?Zeile2")]
    [InlineData("", "")]
    public void Session_attributes_are_ascii(string input, string expected) =>
        Assert.Equal(expected, OracleSession.ToSessionAttribute(input));

    [Fact]
    public void Session_attributes_are_limited_to_64_characters()
    {
        Assert.Equal(64, OracleSession.ToSessionAttribute(new string('x', 100)).Length);
        Assert.Equal(string.Concat(Enumerable.Repeat("Ae", 32)), OracleSession.ToSessionAttribute(new string('Ä', 40)));
    }
}
