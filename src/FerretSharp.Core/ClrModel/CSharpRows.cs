using System.Text;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// Rows as C# (WP-15): object initializers of the entity (<c>var kunde = new Kunde { … };</c>, several rows as a list with a
/// collection expression) or an EF Core seed (<c>builder.HasData(…)</c>). Values through <see cref="CSharpCode"/>; NULL
/// values are left out, navigations are never set (their FK properties are). Columns without a property and values
/// without a C# form become comments, and the warnings name them.
/// </summary>
public static class CSharpRows
{
    private const string Indent = "    ";

    /// <summary>Why the rows of a table cannot be seeded; null if they can.</summary>
    public static string? HasDataUnavailable(TablePresentation presentation) => presentation.Entity switch
    {
        null => LinqFilter.Unavailable(presentation),
        { Entity.PrimaryKey.Count: 0 } => "HasData braucht einen Schlüssel – die Entity ist keyless.",
        { Entity.Table: null } => "HasData geht nur für Entities auf Tabellen, nicht auf Views.",
        { Entity.IsOwned: true } => "Owned-Typen werden über ihren Besitzer geseedet (OwnsOne(…).HasData).",
        _ => null,
    };

    /// <summary>One row: <c>var kunde = new Kunde { … };</c>; several: <c>List&lt;Kunde&gt; kunden = [ new() { … }, … ];</c>.</summary>
    public static ExportText Initializers(TablePresentation presentation, IReadOnlyList<RowData> rows)
    {
        var (entity, notes) = Prepare(presentation);
        var text = new StringBuilder();
        if (rows.Count == 1)
        {
            text.Append("var ").Append(CSharpCode.LocalName(entity)).Append(" = new ").Append(entity).Append("\r\n");
            AppendBody(text, presentation, rows[0], "", seed: false, notes);
            text.Append(";");
        }
        else
        {
            text.Append("List<").Append(entity).Append("> ").Append(ListName(presentation, entity)).Append(" =\r\n[\r\n");
            foreach (var row in rows)
            {
                text.Append(Indent).Append("new()\r\n");
                AppendBody(text, presentation, row, Indent, seed: false, notes);
                text.Append(",\r\n");
            }

            text.Append("];");
        }

        return new ExportText(text.ToString(), rows.Count, notes.ToList());
    }

    /// <summary>
    /// <c>builder.HasData(new Kunde { … }, …);</c> for an <c>IEntityTypeConfiguration</c>. Rows with values of shadow
    /// properties become anonymous objects, the only way EF accepts those.
    /// </summary>
    public static ExportText HasData(TablePresentation presentation, IReadOnlyList<RowData> rows)
    {
        if (HasDataUnavailable(presentation) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var (entity, notes) = Prepare(presentation);
        var text = new StringBuilder("builder.HasData(");
        for (var i = 0; i < rows.Count; i++)
        {
            var anonymous = HasShadowValues(presentation, rows[i]);
            text.Append(i == 0 ? "\r\n" : ",\r\n").Append(Indent).Append(anonymous ? "new" : "new " + entity).Append("\r\n");
            AppendBody(text, presentation, rows[i], Indent, seed: anonymous, notes);
        }

        text.Append(");");
        return new ExportText(text.ToString(), rows.Count, notes.ToList());
    }

    /// <summary>The C# value of a single cell (<c>Kundenart.Gewerbe</c>); a problem if the column has no property or the value no C# form.</summary>
    public static CSharpValue Cell(TablePresentation presentation, int column, object? raw) =>
        presentation.PropertyOf(column) is { } property
            ? CSharpCode.Value(property, presentation.ValuesOf(column), presentation.Details.Columns[column], raw)
            : CSharpValue.Fails($"{presentation.Details.Columns[column].Name} hat keine Property im C#-Modell.");

    private static (string Entity, Notes Notes) Prepare(TablePresentation presentation)
    {
        if (LinqFilter.Unavailable(presentation) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        return (presentation.EntityName!, new Notes());
    }

    /// <summary><c>kunden</c> from the DbSet, else <c>kundeList</c>.</summary>
    private static string ListName(TablePresentation presentation, string entity) =>
        presentation.Entity!.Entity.DbSet is { } dbSet && dbSet != entity ? CSharpCode.LocalName(dbSet) : CSharpCode.LocalName(entity + "List");

    private static bool HasShadowValues(TablePresentation presentation, RowData row)
    {
        for (var i = 0; i < presentation.Details.Columns.Count; i++)
        {
            if (row.Values[i] is not (null or DBNull) && presentation.IsEntityProperty(i) && presentation.PropertyOf(i)!.IsShadow)
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="seed">An anonymous object of a seed: shadow properties can be set.</param>
    private static void AppendBody(StringBuilder text, TablePresentation presentation, RowData row, string indent, bool seed, Notes notes)
    {
        var inner = indent + Indent;
        text.Append(indent).Append("{\r\n");
        for (var i = 0; i < presentation.Details.Columns.Count; i++)
        {
            var raw = row.Values[i];
            if (raw is null or DBNull)
            {
                continue;
            }

            var column = presentation.Details.Columns[i];
            if (presentation.PropertyOf(i) is not { } property)
            {
                text.Append(inner).Append("// ").Append(column.Name).Append(" = ").Append(CSharpCode.Shown(column, raw))
                    .Append(" (keine Property)\r\n");
                notes.Add(column.Name, "keine Property");
                continue;
            }

            if (!presentation.IsEntityProperty(i))
            {
                text.Append(inner).Append("// ").Append(column.Name).Append(" = ").Append(CSharpCode.Shown(column, raw))
                    .Append(" (Property ").Append(property.Name).Append(" eines anderen Entity-Typs der Tabelle)\r\n");
                notes.Add(column.Name, $"Property {property.Name} eines anderen Entity-Typs");
                continue;
            }

            var value = CSharpCode.Value(property, presentation.ValuesOf(i), column, raw);
            if (value.Code is null)
            {
                text.Append(inner).Append("// ").Append(property.Name).Append(": ").Append(value.Problem).Append("\r\n");
                notes.Add(property.Name, value.Problem!);
                continue;
            }

            if (property.IsShadow && !seed)
            {
                text.Append(inner).Append("// ").Append(property.Name).Append(" = ").Append(value.Code)
                    .Append(" (Shadow-Property)\r\n");
                notes.Add(property.Name, "Shadow-Property, im Initializer nicht setzbar");
                continue;
            }

            text.Append(inner).Append(CSharpCode.Identifier(property.Name)).Append(" = ").Append(value.Code).Append(",\r\n");
        }

        text.Append(indent).Append('}');
    }

    /// <summary>What did not make it into the code, counted per member and reason.</summary>
    private sealed class Notes
    {
        private readonly Dictionary<(string Member, string Reason), int> _counts = [];

        public void Add(string member, string reason) => _counts[(member, reason)] = _counts.GetValueOrDefault((member, reason)) + 1;

        public List<string> ToList()
        {
            const int max = 8;
            var notes = _counts.Take(max)
                .Select(kv => $"{kv.Key.Member}: {kv.Key.Reason}{(kv.Value > 1 ? $" ({kv.Value} Zeilen)" : "")} – als Kommentar.")
                .ToList();
            if (_counts.Count > max)
            {
                notes.Add($"… und {_counts.Count - max} weitere, siehe Kommentare im Code.");
            }

            return notes;
        }
    }
}
