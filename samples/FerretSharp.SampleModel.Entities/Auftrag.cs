namespace FerretSharp.SampleModel.Entities;

public class Auftrag
{
    public int AuftragId { get; set; }

    public int KundeId { get; set; }

    public AuftragStatus Status { get; set; }

    public string? Notiz { get; set; }

    public Kunde Kunde { get; set; } = null!;

    public List<AuftragPosition> Positionen { get; set; } = [];
}
