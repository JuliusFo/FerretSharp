// The model of a user's EF Core project as FerretSharp.ModelHost writes it (ADR 0009). This file is compiled into both
// FerretSharp.Core and FerretSharp.ModelHost (net8.0, C# 12), so it stays plain: records and System.Text.Json only.
using System.Text.Json;

namespace FerretSharp.Core.ClrModel;

/// <summary>Result of one ModelHost run: the model or what went wrong.</summary>
public sealed record ModelHostResult(ModelExport? Model, ModelHostError? Error)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

/// <param name="Kind">One of <see cref="ModelHostErrorKind"/>.</param>
/// <param name="Detail">Technical detail (exception type and stack, inner exceptions) for the log and "Details".</param>
public sealed record ModelHostError(string Kind, string Message, string? Detail = null);

public static class ModelHostErrorKind
{
    public const string Arguments = "Arguments";
    public const string AssemblyNotLoadable = "AssemblyNotLoadable";
    public const string NoContext = "NoContext";
    public const string SeveralContexts = "SeveralContexts";
    public const string NoProvider = "NoProvider";
    public const string ConstructorNeedsServices = "ConstructorNeedsServices";
    public const string ContextCreationFailed = "ContextCreationFailed";
    public const string ModelBuildFailed = "ModelBuildFailed";
    public const string Unexpected = "Unexpected";
}

/// <param name="EfVersion">Informational version of Microsoft.EntityFrameworkCore as loaded, e.g. <c>8.0.11</c>.</param>
/// <param name="CreatedBy">How the context was created: <c>factory</c>, <c>options</c> or <c>parameterless</c>.</param>
/// <param name="DefaultSchema">The model's default schema (<c>HasDefaultSchema</c>); null = the connection's schema.</param>
public sealed record ModelExport(
    int FormatVersion,
    string EfVersion,
    string ContextType,
    string CreatedBy,
    string? DefaultSchema,
    IReadOnlyList<EntityExport> Entities)
{
    public const int CurrentFormatVersion = 1;
}

/// <param name="Name">Entity name as EF knows it (for shared-type entities not the CLR name).</param>
/// <param name="ClrType">Full CLR type name, e.g. <c>Shop.Entities.Kunde</c>.</param>
/// <param name="Table">Mapped table; null if mapped to a view only or not at all (keyless query types).</param>
public sealed record EntityExport(
    string Name,
    string ClrType,
    bool IsOwned,
    string? BaseType,
    string? Table,
    string? Schema,
    string? View,
    string? ViewSchema,
    IReadOnlyList<PropertyExport> Properties,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyExport> ForeignKeys);

/// <param name="ClrType">C# type as written, without nullability: <c>int</c>, <c>string</c>, <c>AuftragStatus</c>.</param>
/// <param name="ClrTypeFull">Full CLR name, e.g. <c>Shop.Entities.AuftragStatus</c>.</param>
/// <param name="Column">Column in the entity's table (or view).</param>
/// <param name="Converter">Value converter type, e.g. <c>JaNeinConverter</c>; null without one.</param>
/// <param name="ProviderClrType">Type the converter stores, e.g. <c>string</c>.</param>
/// <param name="Values">For enums and converted bools: every C# value with its database value.</param>
public sealed record PropertyExport(
    string Name,
    string ClrType,
    string ClrTypeFull,
    bool Nullable,
    bool IsShadow,
    string? Column,
    string? ColumnType,
    string? Converter,
    string? ProviderClrType,
    bool IsFlagsEnum,
    IReadOnlyList<ValueMapping>? Values);

/// <param name="Name">Enum member (<c>Offen</c>) or <c>true</c>/<c>false</c>.</param>
/// <param name="ClrValue">The C# value, invariant: the enum's number, <c>True</c>/<c>False</c>.</param>
/// <param name="ProviderValue">What is stored in the database, invariant text (<c>OFFEN</c>, <c>J</c>, <c>2</c>); null for NULL.</param>
public sealed record ValueMapping(string Name, string ClrValue, string? ProviderValue);

/// <param name="Properties">The dependent's FK properties.</param>
/// <param name="Navigation">Navigation on the dependent (<c>Auftrag.Kunde</c>); null without one.</param>
/// <param name="InverseNavigation">Navigation on the principal (<c>Kunde.Auftraege</c>); null without one.</param>
public sealed record ForeignKeyExport(
    IReadOnlyList<string> Properties,
    string PrincipalEntity,
    IReadOnlyList<string> PrincipalProperties,
    string? Navigation,
    string? InverseNavigation,
    bool IsUnique);
