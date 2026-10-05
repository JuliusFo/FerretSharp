using System.ComponentModel.DataAnnotations;

namespace FerretSharp.SampleModel.Entities;

/// <summary>Art des Kunden; in der DB als Zahl gespeichert. Nur Behörde hat einen Anzeigetext (ohne Ressourcen).</summary>
public enum Kundenart
{
    Privat = 1,
    Gewerbe = 2,

    [Display(Name = "Behörde")]
    Behoerde = 3,
}
