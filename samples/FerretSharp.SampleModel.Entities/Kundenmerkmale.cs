using System.ComponentModel.DataAnnotations;

namespace FerretSharp.SampleModel.Entities;

/// <summary>Merkmale eines Kunden, beliebig kombinierbar; in der DB als Summe der Flags gespeichert (EF ohne Converter).</summary>
[Flags]
public enum Kundenmerkmale
{
    Keine = 0,
    Stammkunde = 1,
    Newsletter = 2,

    [Display(Name = "Zahlt per Lastschrift")]
    Lastschrift = 4,

    Export = 8,

    /// <summary>Eine Kombination: zählt nicht als eigenes Flag.</summary>
    Premium = Stammkunde | Lastschrift,
}
