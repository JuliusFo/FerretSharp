namespace FerretSharp.UI.State;

/// <summary>What <c>STATUS = 'INVALID'</c> means for the user, by object type (tooltip of the red markers).</summary>
public static class InvalidStatusText
{
    /// <param name="objectType"><c>ALL_OBJECTS.OBJECT_TYPE</c>, e.g. VIEW, MATERIALIZED VIEW, PACKAGE BODY.</param>
    public static string For(string objectType) => objectType switch
    {
        "MATERIALIZED VIEW" =>
            "Ungültig (INVALID): muss neu kompiliert werden, z. B. nach einer Änderung an der Basistabelle. Abfragen funktionieren weiter.",
        "VIEW" =>
            "Ungültig (INVALID): Oracle kompiliert die View beim nächsten Zugriff neu. Schlägt das fehl (z. B. weil eine Spalte fehlt), liefert die Abfrage einen Fehler.",
        _ =>
            "Ungültig (INVALID): wird beim nächsten Aufruf neu kompiliert. Schlägt das fehl, gibt es einen Fehler.",
    };
}
