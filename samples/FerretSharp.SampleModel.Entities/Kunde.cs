namespace FerretSharp.SampleModel.Entities;

/// <summary>Ein Kunde mit seinen Aufträgen.</summary>
public class Kunde
{
    public int KundeId { get; set; }

    /// <summary>Firmen- oder Personenname.</summary>
    public string Name { get; set; } = "";

    /// <summary>Dreistelliges Kürzel, eindeutig.</summary>
    public string? Kuerzel { get; set; }

    public DateTime ErstelltAm { get; set; }

    public decimal? Umsatz { get; set; }

    /// <summary>Gesperrte Kunden bekommen keine neuen Aufträge (in der DB 'J'/'N').</summary>
    public bool Gesperrt { get; set; }

    public Kundenart Kundenart { get; set; }

    public Kundenmerkmale Merkmale { get; set; }

    public int? AdresseId { get; set; }

    /// <summary>Drift: die Property gibt es, die Spalte (noch) nicht.</summary>
    public string? Email { get; set; }

    public List<Auftrag> Auftraege { get; set; } = [];
}
