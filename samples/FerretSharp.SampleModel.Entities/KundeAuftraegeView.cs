namespace FerretSharp.SampleModel.Entities;

/// <summary>Read-only: orders per customer from the view V_KUNDEN_AUFTRAEGE.</summary>
public class KundeAuftraegeView
{
    public int KundeId { get; set; }

    public string Name { get; set; } = "";

    public int Anzahl { get; set; }
}
