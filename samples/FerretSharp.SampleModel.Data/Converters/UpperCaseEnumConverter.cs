using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FerretSharp.SampleModel.Data.Converters;

/// <summary>An enum as its name in upper case (<c>AuftragStatus.Offen</c> → <c>'OFFEN'</c>).</summary>
public sealed class UpperCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => v.ToString().ToUpperInvariant(),
    v => Enum.Parse<TEnum>(v, true))
    where TEnum : struct, Enum;
