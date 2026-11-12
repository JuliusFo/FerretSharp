using System.Text;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The C# files for a table without entity (WP-41, second stage, decisions of the user): the entity and its configuration
/// as <see cref="EntityCode.Write"/> has them, each as a new file where most of the project's entities and configurations
/// lie (folder, namespace, file-scoped or block namespace, byte order mark, line ending, indentation from those files);
/// a <c>DbSet</c> in the context if it has others, in the style of its last one; the configuration registered with
/// <c>ApplyConfiguration</c> if the context registers them one by one (nothing with <c>ApplyConfigurationsFromAssembly</c>).
/// The other side of a navigation is only named. A file that exists is never overwritten.
/// </summary>
public static class NewEntityCode
{
    private const string ConfigurationInterface = "IEntityTypeConfiguration";

    /// <summary>The identifiers a scan must find files by: every entity of the model, the context, converters and the new name.</summary>
    public static IReadOnlyList<string> Words(ModelExport model, string name) =>
    [
        .. model.Entities.Select(e => ClrModelMapping.ShortName(e.ClrType)),
        .. model.Entities.SelectMany(e => e.Properties).Select(p => p.Converter).OfType<string>().Select(ConverterName),
        ClrModelMapping.ShortName(model.ContextType), ConfigurationInterface, name, name + "Configuration",
    ];

    /// <param name="entityFolder">The folder (relative to the work clone) the user chose for the entity; null for the most used.</param>
    /// <param name="configurationFolder">The same for the configuration.</param>
    public static CodeChangePlan Plan(ClrModelMapping mapping, TableDetails table, IReadOnlyList<ForeignKeyInfo> outgoing, string name, CodeScan scan,
        string? entityFolder = null, string? configurationFolder = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(scan);
        var model = mapping.Model;
        name = name.Trim();
        if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
        {
            // It becomes the class, the file and the DbSet: no syntax error later, but a reason now (review WP-41).
            return new CodeChangePlan(scan.Root, [], [], [TextFormat.Format(CodeEditText.Problem_InvalidName, name)]);
        }

        var suggestion = EntityCode.Write(mapping, table, outgoing, name);
        name = suggestion.Name;
        var notes = suggestion.Notes.Where(n => n != TextFormat.Format(ClrModelText.EntityDbSet, name)).ToList();
        var problems = new List<string>();
        var edits = new FileEdits(scan.Root);

        // Where the project keeps its entities: the folder most of the model's entity classes are declared in.
        var entitySites = model.Entities.Where(e => !e.IsOwned)
            .Select(e => scan.TypesNamed(e.ClrType).FirstOrDefault())
            .OfType<TypeSite>()
            .ToList();
        if (entitySites.Count == 0)
        {
            return new CodeChangePlan(scan.Root, [], notes, [CodeEditText.Problem_NoEntityFolder]);
        }

        var entityDeclarations = entitySites.Select(s => (s.File, s.Declaration as SyntaxNode)).ToList();
        var entityPlace = Place.Of(entityDeclarations, scan.Root, entityFolder);
        var configurations = scan.Files
            .SelectMany(f => f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.BaseList?.Types.Any(b => b.Type.ToString().Contains(ConfigurationInterface + "<", StringComparison.Ordinal)) == true)
                .Select(c => (File: f, Node: (SyntaxNode)c)))
            .ToList();
        var contextParts = scan.TypesNamed(model.ContextType);
        if (CodeScan.Ambiguous(contextParts) is { } ambiguous)
        {
            problems.Add(ambiguous);
        }

        IReadOnlyList<(ScannedFile File, SyntaxNode Node)> configDeclarations = configurations.Count > 0
            ? configurations
            : contextParts.FirstOrDefault() is { } contextPart ? [(contextPart.File, contextPart.Declaration)] : entityDeclarations;
        var configPlace = Place.Of(configDeclarations, scan.Root, configurationFolder);
        var places = new NewEntityPlaces(Place.Folders(entityDeclarations, scan.Root), Path.GetRelativePath(scan.Root, entityPlace.Folder),
            Place.Folders(configDeclarations, scan.Root), Path.GetRelativePath(scan.Root, configPlace.Folder));

        var entityFile = Path.Combine(entityPlace.Folder, name + ".cs");
        var configName = name + "Configuration";
        var configFile = Path.Combine(configPlace.Folder, configName + ".cs");
        foreach (var file in new[] { entityFile, configFile }.Where(File.Exists))
        {
            problems.Add(TextFormat.Format(CodeEditText.Problem_FileExists, Path.GetRelativePath(scan.Root, file)));
        }

        foreach (var (ns, type) in new[] { (entityPlace.Namespace, name), (configPlace.Namespace, configName) })
        {
            var full = ns.Length == 0 ? type : $"{ns}.{type}";
            if (scan.Types.Any(t => t.FullName == full))
            {
                problems.Add(TextFormat.Format(CodeEditText.Problem_TypeExists, full));
            }
        }

        var nullableReferences = EntityCodeChanges.ProjectNullable(scan.Root, entityPlace.Folder) ?? EntityCodeChanges.NullableReferences(scan);
        var globalUsings = scan.Files.SelectMany(f => f.Root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).Select(u => u.NamespaceOrType.ToString())).ToHashSet(StringComparer.Ordinal);

        // The entity.
        var principals = outgoing.Select(f => mapping.EntityOf(f.To)?.Entity).OfType<EntityExport>().Distinct().ToList();
        var entityUsings = principals.Select(p => NamespaceOf(p.ClrType)).Where(n => n.Length > 0 && n != entityPlace.Namespace);
        var entityText = nullableReferences ? suggestion.Entity : WithoutReferenceNullability(suggestion.Entity, principals);
        edits.Create(Created(scan.Root, entityFile, entityPlace, entityUsings, globalUsings, entityText));

        // The configuration: EF Core, the entity's namespace, the converters it names.
        var converters = model.Entities.SelectMany(e => e.Properties).Select(p => p.Converter).OfType<string>()
            .Select(ConverterName).Distinct(StringComparer.Ordinal)
            .Where(c => suggestion.Configuration.Contains($"<{c}>", StringComparison.Ordinal))
            .ToList();
        var converterUsings = new List<string>();
        foreach (var converter in converters)
        {
            var declared = scan.Types.Where(t => ClrModelMapping.ShortName(t.FullName) == converter).Select(t => t.Namespace).Distinct().ToList();
            if (declared is [var only])
            {
                converterUsings.Add(only);
            }
            else
            {
                notes.Add(TextFormat.Format(CodeEditText.Note_ConverterNamespace, converter));
            }
        }

        string[] configUsings = ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Metadata.Builders", entityPlace.Namespace, .. converterUsings];
        edits.Create(Created(scan.Root, configFile, configPlace, configUsings.Where(n => n.Length > 0 && n != configPlace.Namespace), globalUsings, suggestion.Configuration));

        // The context: DbSet and registration.
        if (contextParts.Count == 0)
        {
            notes.Add(TextFormat.Format(CodeEditText.Note_ContextNotFound, model.ContextType, name));
        }
        else
        {
            DbSet(scan, edits, contextParts, mapping, table, name, entityPlace.Namespace);
            Register(scan, edits, contextParts, configName, configPlace.Namespace, notes);
        }

        foreach (var folder in new[] { entityPlace.Folder, configPlace.Folder }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ProjectOf(scan.Root, folder) is not { } project)
            {
                notes.Add(TextFormat.Format(CodeEditText.Note_NoProject, Path.GetRelativePath(scan.Root, folder)));
            }
            else if (!IsSdkProject(project))
            {
                notes.Add(TextFormat.Format(CodeEditText.Note_OldProject, Path.GetRelativePath(scan.Root, project)));
            }
        }

        foreach (var principal in principals)
        {
            notes.Add(TextFormat.Format(CodeEditText.Note_InverseNavigation, ClrModelMapping.ShortName(principal.ClrType), name));
        }

        var (files, built) = edits.Build();
        problems.AddRange(built);
        return new CodeChangePlan(scan.Root, files, notes, problems) { Places = places };
    }

    /// <summary>A folder new files go to, with the namespace and the way its files are written.</summary>
    private sealed record Place(string Folder, string Namespace, bool FileScoped, CodeFileFormat Format)
    {
        /// <summary>
        /// The folder <paramref name="chosen"/> (relative to <paramref name="root"/>) if declarations are there, else the one most
        /// of them are in – and what most of its files there have.
        /// </summary>
        public static Place Of(IReadOnlyList<(ScannedFile File, SyntaxNode Node)> declarations, string root, string? chosen)
        {
            var groups = Grouped(declarations);
            var folder = groups.FirstOrDefault(g => chosen is not null && string.Equals(Path.GetRelativePath(root, g.Key), chosen, StringComparison.OrdinalIgnoreCase)) ?? groups[0];
            var there = folder.ToList();
            var ns = Most(there.Select(d => string.Join('.', d.Node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()))));
            var fileScoped = there.Count(d => d.Node.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().Any()) * 2 >= there.Count;
            var files = there.Select(d => d.File.File.Format).ToList();
            var format = new CodeFileFormat(Most(files.Select(f => f.Bom)), Most(files.Select(f => f.NewLine)), Most(files.Select(f => f.Indent)));
            return new Place(folder.Key, ns, fileScoped, format);
        }

        /// <summary>The folders of the declarations relative to <paramref name="root"/>, most used first.</summary>
        public static IReadOnlyList<string> Folders(IReadOnlyList<(ScannedFile File, SyntaxNode Node)> declarations, string root) =>
            [.. Grouped(declarations).Select(g => Path.GetRelativePath(root, g.Key))];

        private static List<IGrouping<string, (ScannedFile File, SyntaxNode Node)>> Grouped(IReadOnlyList<(ScannedFile File, SyntaxNode Node)> declarations) =>
            [.. declarations.GroupBy(d => Path.GetDirectoryName(d.File.Path)!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)];

        private static T Most<T>(IEnumerable<T> values) => values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
    }

    /// <summary>A new file: usings, namespace and the type, written as the place's files are.</summary>
    private static FileChange Created(string root, string path, Place place, IEnumerable<string> usings, IReadOnlySet<string> globalUsings, string type)
    {
        var text = new StringBuilder();
        // A parent namespace of the file's own is seen without a using.
        var lines = usings.Where(u => !globalUsings.Contains(u) && place.Namespace != u && !place.Namespace.StartsWith(u + ".", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var line in lines)
        {
            text.Append($"using {line};\n");
        }

        if (lines.Count > 0)
        {
            text.Append('\n');
        }

        if (place.Namespace.Length == 0)
        {
            text.Append(type).Append('\n');
        }
        else if (place.FileScoped)
        {
            text.Append($"namespace {place.Namespace};\n\n").Append(type).Append('\n');
        }
        else
        {
            text.Append($"namespace {place.Namespace}\n{{\n");
            foreach (var line in type.Split('\n'))
            {
                text.Append(line.Length == 0 ? "" : "    " + line).Append('\n');
            }

            text.Append("}\n");
        }

        return new FileChange(path, Path.GetRelativePath(root, path), null, place.Format.Apply(text.ToString()), place.Format);
    }

    /// <summary>A <c>DbSet</c> like the context's last one, if it has any.</summary>
    private static void DbSet(CodeScan scan, FileEdits edits, IReadOnlyList<TypeSite> context, ClrModelMapping mapping, TableDetails table, string name,
        string entityNamespace)
    {
        var sets = context.SelectMany(c => c.Declaration.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Type is GenericNameSyntax { Identifier.ValueText: "DbSet" }).Select(p => (c, p))).ToList();
        if (sets.Count == 0)
        {
            return; // the context uses Set<T>() – nothing to add
        }

        var (part, last) = sets[^1];
        var setName = DbSetName(mapping.Model, table, name);
        var members = context.SelectMany(c => c.Declaration.Members).OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        for (var n = 2; members.Contains(setName); n++)
        {
            setName = DbSetName(mapping.Model, table, name) + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var oldType = ((GenericNameSyntax)last.Type).TypeArgumentList.Arguments[0].ToString();
        var text = part.File.File.Text;
        var start = FileEdits.LineStart(text, last.SpanStart);
        // The last DbSet's text with the name and the entity exchanged – also in its body (=> Set<Kunde>()), but only where a
        // type is written, not in a string of an attribute (review WP-41): modifiers, accessors, initializer stay as they are.
        var types = last.DescendantNodes().OfType<TypeSyntax>().Where(t => t.ToString() == oldType).ToList();
        var exchanges = types.Where(t => !types.Any(o => o != t && o.Span.Contains(t.Span)))
            .Select(t => (t.Span, Text: name))
            .Append((last.Identifier.Span, Text: setName))
            .OrderByDescending(e => e.Span.Start)
            .ToList();
        var copy = text[start..last.Span.End];
        foreach (var (span, replacement) in exchanges)
        {
            copy = copy[..(span.Start - start)] + replacement + copy[(span.End - start)..];
        }

        edits.Insert(part.File, FileEdits.LineEnd(text, last.Span.End), copy + part.File.File.Format.NewLine);
        AddUsing(scan, edits, part.File, last, entityNamespace);
    }

    /// <summary>The name of a new DbSet as most of the context's DbSets are named: like the entity, the table or the entity plus "s".</summary>
    internal static string DbSetName(ModelExport model, TableDetails table, string name)
    {
        var named = model.Entities.Where(e => e is { DbSet: not null, IsOwned: false }).ToList();
        if (named.Count == 0)
        {
            return name;
        }

        var asEntity = named.Count(e => e.DbSet == ClrModelMapping.ShortName(e.ClrType));
        var asTable = named.Count(e => e.Table is { } t && e.DbSet == PropertyCode.PascalCase(t));
        var withS = named.Count(e => e.DbSet == ClrModelMapping.ShortName(e.ClrType) + "s");
        var best = Math.Max(asEntity, Math.Max(asTable, withS));
        return best == asEntity ? name
            : best == asTable ? CSharpCode.Identifier(PropertyCode.PascalCase(table.Table.Name))
            : name + "s";
    }

    /// <summary>
    /// <c>ApplyConfiguration(new XConfiguration())</c> behind the last one in <c>OnModelCreating</c>; nothing where the context
    /// takes the configurations from the assembly, a note where it does neither.
    /// </summary>
    private static void Register(CodeScan scan, FileEdits edits, IReadOnlyList<TypeSite> context, string configName, string configNamespace, List<string> notes)
    {
        var onModelCreating = context.SelectMany(c => c.Declaration.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "OnModelCreating" && m.Body is not null).Select(m => (c, m))).FirstOrDefault();
        if (onModelCreating.m is not { Body: { } body } method)
        {
            notes.Add(TextFormat.Format(CodeEditText.Note_Register, configName));
            return;
        }

        var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        if (calls.Any(c => c.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ApplyConfigurationsFromAssembly" }))
        {
            return;
        }

        var registered = body.Statements.OfType<ExpressionStatementSyntax>()
            .LastOrDefault(s => s.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ApplyConfiguration" } });
        if (registered?.Expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access })
        {
            notes.Add(TextFormat.Format(CodeEditText.Note_Register, configName));
            return;
        }

        var file = onModelCreating.c.File;
        var text = file.File.Text;
        var line = $"{FileEdits.IndentAt(text, registered.SpanStart)}{access.Expression}.ApplyConfiguration(new {configName}());{file.File.Format.NewLine}";
        edits.Insert(file, FileEdits.LineEnd(text, registered.Span.End), line);
        AddUsing(scan, edits, file, method, configNamespace);
    }

    /// <summary>A <c>using</c> for <paramref name="ns"/> behind the file's last one, if the file does not see the namespace yet.</summary>
    private static void AddUsing(CodeScan scan, FileEdits edits, ScannedFile file, SyntaxNode at, string ns)
    {
        if (ns.Length == 0 || scan.Sees(file, at, ns))
        {
            return;
        }

        var text = file.File.Text;
        var line = $"using {ns};{file.File.Format.NewLine}";
        if (file.Root.Usings.LastOrDefault() is { } last)
        {
            edits.Insert(file, FileEdits.LineEnd(text, last.Span.End), line);
        }
        else
        {
            edits.Insert(file, 0, line + file.File.Format.NewLine);
        }
    }

    /// <summary>The entity text for a project without nullable reference types: no <c>?</c> on strings, arrays and navigations.</summary>
    private static string WithoutReferenceNullability(string text, IReadOnlyList<EntityExport> principals)
    {
        text = EntityCodeChanges.ReferenceNullable().Replace(text, "$1 ");
        foreach (var principal in principals)
        {
            text = text.Replace($"public {ClrModelMapping.ShortName(principal.ClrType)}? ", $"public {ClrModelMapping.ShortName(principal.ClrType)} ", StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>The project file nearest above <paramref name="folder"/>, not above the work clone.</summary>
    private static string? ProjectOf(string root, string folder)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = Path.GetFullPath(folder); current is not null && current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (Directory.GetFiles(current, "*.csproj") is [var project, ..])
                {
                    return project;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>An SDK-style project compiles every <c>.cs</c> file below it; an old one only those it lists.</summary>
    private static bool IsSdkProject(string project)
    {
        try
        {
            var text = File.ReadAllText(project);
            return text.Contains("Sdk=\"", StringComparison.OrdinalIgnoreCase) || text.Contains("<Sdk ", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // not to be read: no warning on a guess
        }
    }

    private static string NamespaceOf(string clrType) =>
        clrType.LastIndexOf('.') is var dot and > 0 && !clrType[..dot].Contains('+', StringComparison.Ordinal) ? clrType[..dot] : "";

    /// <summary><c>Shop.Data.JaNeinConverter</c> → <c>JaNeinConverter</c>; generic converters by their name before <c>&lt;</c>.</summary>
    private static string ConverterName(string converter)
    {
        var generic = converter.IndexOf('<', StringComparison.Ordinal);
        return ClrModelMapping.ShortName(generic >= 0 ? converter[..generic] : converter);
    }
}
