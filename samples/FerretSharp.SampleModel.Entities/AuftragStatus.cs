using System.ComponentModel.DataAnnotations;
using FerretSharp.SampleModel.Entities.Resources;

namespace FerretSharp.SampleModel.Entities;

/// <summary>Status eines Auftrags; in der DB als Text in Großbuchstaben (eigener Converter), Anzeigetexte aus Ressourcen.</summary>
public enum AuftragStatus
{
    [Display(ResourceType = typeof(EnumTexts), Name = nameof(EnumTexts.Offen))]
    Offen,

    [Display(ResourceType = typeof(EnumTexts), Name = nameof(EnumTexts.Versandt))]
    Versandt,

    [Display(ResourceType = typeof(EnumTexts), Name = nameof(EnumTexts.Storniert))]
    Storniert,

    [Display(ResourceType = typeof(EnumTexts), Name = nameof(EnumTexts.Abgeschlossen))]
    Abgeschlossen,
}
