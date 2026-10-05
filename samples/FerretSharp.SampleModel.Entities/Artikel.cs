namespace FerretSharp.SampleModel.Entities;

public class Artikel : Stammdaten
{
    public int? HerstellerId { get; set; }

    public int? KategorieId { get; set; }

    public Hersteller? Hersteller { get; set; }

    public Kategorie? Kategorie { get; set; }
}

public class Hersteller : Stammdaten;

public class Kategorie : Stammdaten;
