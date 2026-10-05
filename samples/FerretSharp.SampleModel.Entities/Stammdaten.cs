namespace FerretSharp.SampleModel.Entities;

/// <summary>Gemeinsame Spalten aller Stammdatentabellen (ID, BEZEICHNUNG, AKTIV, ERSTELLT_AM, GEAENDERT_AM).</summary>
public abstract class Stammdaten
{
    public int Id { get; set; }

    public string Bezeichnung { get; set; } = "";

    public bool Aktiv { get; set; }

    public DateTime? ErstelltAm { get; set; }

    public DateTime? GeaendertAm { get; set; }
}
