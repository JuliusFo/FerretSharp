using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FerretSharp.SampleModel.Data.Converters;

/// <summary>bool as 'J'/'N' in a CHAR(1) column.</summary>
public sealed class JaNeinConverter() : ValueConverter<bool, string>(v => v ? "J" : "N", v => v == "J");
