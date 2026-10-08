using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

public enum ColumnMismatchKind
{
    /// <summary>The stored CLR type (after the converter) cannot be read from the column's Oracle type.</summary>
    Type,

    /// <summary>NULL allowed on one side only.</summary>
    Nullability,

    /// <summary>Configured maximum length against the column's length (BYTE/CHAR semantics included).</summary>
    Length,

    /// <summary>Precision/scale of a number against the CLR type or the configured precision.</summary>
    Precision,
}

public enum MismatchSeverity
{
    /// <summary>Every query reading the property fails.</summary>
    Error,

    /// <summary>Fails or loses data for some values (NULL, too long, too many digits).</summary>
    Warning,

    /// <summary>Model and database differ without an error to expect.</summary>
    Hint,
}

/// <summary>A property whose column does not fit it: type, NULL, length or precision.</summary>
/// <param name="Message">What follows from it, e.g. „EF Core wirft beim Lesen einer Zeile mit NULL“.</param>
public sealed record ColumnMismatch(
    ColumnMismatchKind Kind,
    MismatchSeverity Severity,
    EntityExport Entity,
    PropertyExport Property,
    TableSummary Table,
    ColumnInfo Column,
    string Message);

/// <summary>
/// Compares a property of the C# model with the column it maps to. Uses what the database stores (the converter's
/// provider type, <c>J</c>/<c>N</c> for a bool) and only facets the project configured: the provider's defaults
/// (<c>NVARCHAR2(2000)</c> for every string) would make nearly every column differ.
/// </summary>
public static class ColumnTypeCheck
{
    private enum StoredKind { Unknown, Text, Char, Bool, Integer, Decimal, Float, DateTime, DateOnly, DateTimeOffset, TimeSpan, Guid, Bytes }

    /// <param name="readOnly">The column belongs to a view or materialized view: only reading is checked.</param>
    public static IEnumerable<ColumnMismatch> Check(EntityExport entity, PropertyExport property, TableSummary table, ColumnInfo column, bool readOnly)
    {
        var category = ColumnCategories.Of(column);
        var (kind, digits) = Stored(property);
        if (category == ColumnCategory.Unsupported || kind == StoredKind.Unknown)
        {
            yield break; // XMLTYPE, JSON, object types, spatial and custom CLR types: nothing reliable to say
        }

        ColumnMismatch Mismatch(ColumnMismatchKind k, MismatchSeverity severity, string message) =>
            new(k, severity, entity, property, table, column, message);

        if (TypeProblem(kind, property, category, column) is { } typeProblem)
        {
            yield return Mismatch(ColumnMismatchKind.Type, typeProblem.Severity, typeProblem.Message);
            yield break; // facets of incompatible types say nothing more
        }

        if (NullProblem(property, column, readOnly, table.Kind) is { } nullProblem)
        {
            yield return Mismatch(ColumnMismatchKind.Nullability, nullProblem.Severity, nullProblem.Message);
        }

        if (!readOnly && LengthProblem(kind, property, column) is { } lengthProblem)
        {
            yield return Mismatch(ColumnMismatchKind.Length, lengthProblem.Severity, lengthProblem.Message);
        }

        if (PrecisionProblem(kind, digits, property, column, category, readOnly) is { } precisionProblem)
        {
            yield return Mismatch(ColumnMismatchKind.Precision, precisionProblem.Severity, precisionProblem.Message);
        }
    }

    /// <summary>The property as the model sees it: <c>string(100)</c>, <c>decimal(12,2)?</c>, <c>bool → string(1) (JaNeinConverter)</c>.</summary>
    public static string Describe(PropertyExport property)
    {
        // The facets describe the column, so they belong to what is stored.
        var facets = property switch
        {
            { Precision: { } p, Scale: { } s } => $"({p},{s})",
            { Precision: { } p } => $"({p})",
            { MaxLength: { } length } => $"({length})",
            _ => "",
        };
        var nullable = property.ColumnNullable ?? property.Nullable ? "?" : "";
        return property is { Converter: { } converter, ProviderClrType: { } provider }
            ? $"{property.ClrType}{nullable} → {provider}{facets} ({converter})"
            : $"{property.ClrType}{facets}{nullable}";
    }

    /// <summary>What is stored: the converter's provider type (also EF's own enum converter), otherwise the CLR type.</summary>
    private static (StoredKind Kind, int Digits) Stored(PropertyExport property)
    {
        var type = property.ProviderClrType ?? property.ClrType;
        return type switch
        {
            "string" => (StoredKind.Text, 0),
            "char" => (StoredKind.Char, 0),
            "bool" => (StoredKind.Bool, 0),
            "byte" or "sbyte" => (StoredKind.Integer, 3),
            "short" or "ushort" => (StoredKind.Integer, 5),
            "int" or "uint" => (StoredKind.Integer, 10),
            "long" => (StoredKind.Integer, 19),
            "ulong" => (StoredKind.Integer, 20),
            "decimal" => (StoredKind.Decimal, 28),
            "double" or "float" => (StoredKind.Float, 0),
            "DateTime" => (StoredKind.DateTime, 0),
            "DateOnly" => (StoredKind.DateOnly, 0),
            "DateTimeOffset" => (StoredKind.DateTimeOffset, 0),
            "TimeSpan" => (StoredKind.TimeSpan, 0),
            "Guid" => (StoredKind.Guid, 0),
            "byte[]" => (StoredKind.Bytes, 0),
            _ => (StoredKind.Unknown, 0),
        };
    }

    private static (MismatchSeverity Severity, string Message)? TypeProblem(StoredKind kind, PropertyExport property, ColumnCategory category, ColumnInfo column)
    {
        var fits = kind switch
        {
            StoredKind.Text => category is ColumnCategory.Text or ColumnCategory.Clob or ColumnCategory.Long,
            StoredKind.Char => category is ColumnCategory.Text,
            StoredKind.Bool => category is ColumnCategory.Number or ColumnCategory.Boolean,
            StoredKind.Integer or StoredKind.Decimal or StoredKind.Float => category is ColumnCategory.Number,
            StoredKind.DateTime => category is ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone,
            StoredKind.DateOnly => category is ColumnCategory.Date or ColumnCategory.Timestamp,
            StoredKind.DateTimeOffset => category is ColumnCategory.TimestampWithTimeZone,
            StoredKind.TimeSpan => category is ColumnCategory.Interval,
            StoredKind.Guid => category is ColumnCategory.Raw,
            StoredKind.Bytes => category is ColumnCategory.Raw or ColumnCategory.Blob or ColumnCategory.Long,
            _ => true,
        };

        if (!fits)
        {
            // EF's own bool mapping may store a number: the CLR type tells whether a converter was forgotten.
            var hint = property is { ClrType: "bool", Converter: null } && category == ColumnCategory.Text ? " – fehlt der Converter (z. B. J/N)?" : "";
            return (MismatchSeverity.Error, $"Der gespeicherte Typ passt nicht zur Spalte {column.DisplayType}: Lesen scheitert{hint}.");
        }

        if (kind == StoredKind.DateTime && category == ColumnCategory.TimestampWithTimeZone)
        {
            return (MismatchSeverity.Hint, "DateTime verliert die Zeitzone der Spalte – DateTimeOffset passt dazu.");
        }

        if (kind == StoredKind.Guid && column.Length is { } length and not 16)
        {
            return (MismatchSeverity.Warning, $"Eine Guid hat 16 Byte, die Spalte RAW({length}).");
        }

        return null;
    }

    private static (MismatchSeverity Severity, string Message)? NullProblem(PropertyExport property, ColumnInfo column, bool readOnly, TableKind kind)
    {
        // EF's view of the column: a required property of a derived type in a shared (TPH) table has a nullable column.
        var modelNullable = property.ColumnNullable ?? property.Nullable;
        if (column.Nullable && !modelNullable)
        {
            return readOnly
                ? (MismatchSeverity.Hint, $"Die Spalte der {(kind == TableKind.View ? "View" : "materialisierten View")} erlaubt NULL, im Modell Pflicht – enthält sie NULL, wirft EF Core beim Lesen. (Oracle meldet berechnete View-Spalten immer als nullable.)")
                : (MismatchSeverity.Warning, "Die Spalte erlaubt NULL, im Modell Pflicht – EF Core wirft beim Lesen einer Zeile mit NULL.");
        }

        // A string or byte[] optional in the model is no difference on Oracle: an empty value is NULL there, so a required
        // property does not keep NULL out of the column either – reading a NOT NULL column into it is fine.
        if (!readOnly && !column.Nullable && modelNullable && property.ClrType is not ("string" or "byte[]")
            && !column.IsIdentity && !column.DefaultOnNull && !column.IsVirtual)
        {
            return (MismatchSeverity.Warning, "Die Spalte ist NOT NULL, im Modell optional – SaveChanges mit NULL scheitert (ORA-01400).");
        }

        return null;
    }

    private static (MismatchSeverity Severity, string Message)? LengthProblem(StoredKind kind, PropertyExport property, ColumnInfo column)
    {
        if (property.MaxLength is not { } maxLength || column.Length is not { } length
            || !(kind is StoredKind.Text or StoredKind.Char && OracleTypes.IsCharacter(column.DataType)
                 || kind == StoredKind.Bytes && column.DataType == "RAW"))
        {
            return null;
        }

        var bytes = OracleTypes.LengthInBytes(column);
        var unit = column.DataType == "RAW" ? "Byte" : "Zeichen";
        if (maxLength > length)
        {
            return (MismatchSeverity.Warning, $"Im Modell bis {maxLength} {unit}, die Spalte fasst {length}{(bytes && unit == "Zeichen" ? " Byte" : "")} – längere Werte scheitern beim Speichern (ORA-12899).");
        }

        if (bytes && unit == "Zeichen")
        {
            // Converted values ('J'/'N', enum codes) come from the converter and are plain ASCII.
            return maxLength == length && property.Converter is null
                ? (MismatchSeverity.Hint, $"Die Spalte zählt Byte (BYTE-Semantik): {maxLength} Zeichen mit Umlauten brauchen mehr als {length} Byte (ORA-12899).")
                : null;
        }

        return maxLength < length
            ? (MismatchSeverity.Hint, $"Im Modell bis {maxLength} {unit}, die Spalte fasst {length}.")
            : null;
    }

    private static (MismatchSeverity Severity, string Message)? PrecisionProblem(
        StoredKind kind, int digits, PropertyExport property, ColumnInfo column, ColumnCategory category, bool readOnly)
    {
        if (category != ColumnCategory.Number || column.DataType != "NUMBER")
        {
            return kind == StoredKind.Integer && column.DataType is "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE"
                ? (MismatchSeverity.Warning, $"Ganzzahl im Modell, die Spalte {column.DisplayType} kann Nachkommastellen enthalten – Lesen scheitert oder schneidet ab.")
                : null;
        }

        switch (kind)
        {
            case StoredKind.Integer when column.Scale is > 0:
                return (MismatchSeverity.Warning, $"Ganzzahl im Modell, die Spalte hat {column.Scale} Nachkommastellen – solche Werte lassen sich nicht lesen.");

            case StoredKind.Integer when column.Precision is { } p && p > digits:
                return (MismatchSeverity.Warning, $"{property.ProviderClrType ?? property.ClrType} fasst {digits} Stellen, die Spalte {p} – größere Werte lassen sich nicht lesen.");

            case StoredKind.Decimal:
                if (property.Precision is { } modelPrecision && column.Precision is { } columnPrecision)
                {
                    var modelScale = property.Scale ?? 0;
                    var columnScale = column.Scale ?? 0;
                    if (!readOnly && modelPrecision - modelScale > columnPrecision - columnScale)
                    {
                        return (MismatchSeverity.Warning, $"Im Modell ({modelPrecision},{modelScale}), die Spalte ({columnPrecision},{columnScale}) – Werte mit mehr als {columnPrecision - columnScale} Vorkommastellen scheitern beim Speichern (ORA-01438).");
                    }

                    if (!readOnly && modelScale > columnScale)
                    {
                        return (MismatchSeverity.Warning, $"Im Modell {modelScale} Nachkommastellen, die Spalte {columnScale} – Oracle rundet beim Speichern.");
                    }

                    if (modelPrecision != columnPrecision || modelScale != columnScale)
                    {
                        return (MismatchSeverity.Hint, $"Im Modell ({modelPrecision},{modelScale}), die Spalte ({columnPrecision},{columnScale}).");
                    }
                }
                else if (column.Precision is { } wide && wide > digits)
                {
                    return (MismatchSeverity.Warning, $"decimal fasst {digits} Stellen, die Spalte {wide} – größere Werte lassen sich nicht lesen.");
                }

                return null;

            default:
                return null;
        }
    }
}
