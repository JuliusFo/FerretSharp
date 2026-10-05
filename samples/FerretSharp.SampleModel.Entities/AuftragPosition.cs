namespace FerretSharp.SampleModel.Entities;

public class AuftragPosition
{
    public int AuftragId { get; set; }

    public int PosNr { get; set; }

    public decimal? Menge { get; set; }

    public int? ArtikelId { get; set; }

    public Auftrag Auftrag { get; set; } = null!;

    public Artikel? Artikel { get; set; }
}
