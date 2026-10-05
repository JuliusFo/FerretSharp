namespace FerretSharp.SampleModel.Entities;

/// <summary>Status eines Auftrags; in der DB als Text in Großbuchstaben (eigener Converter).</summary>
public enum AuftragStatus
{
    Offen,
    Versandt,
    Storniert,
    Abgeschlossen,
}
