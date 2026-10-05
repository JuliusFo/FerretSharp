namespace FerretSharp.SampleModel.Entities;

public class Auftrag
{
    public int AuftragId { get; set; }

    public int KundeId { get; set; }

    public AuftragStatus Status { get; set; }

    public string? Notiz { get; set; }

    /// <summary>Wer den Auftrag bearbeitet; in der DB ohne FK-Constraint (nur das Modell kennt die Beziehung).</summary>
    public int? BearbeiterId { get; set; }

    public Kunde Kunde { get; set; } = null!;

    public Mitarbeiter? Bearbeiter { get; set; }

    public List<AuftragPosition> Positionen { get; set; } = [];
}
