using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FerretSharp.Core.ClrModel;

/// <summary>A C# file of the work clone, read and parsed (syntax only, WP-41).</summary>
/// <param name="Relative">Its path below the work clone, as shown.</param>
public sealed record ScannedFile(CodeFile File, string Relative, CompilationUnitSyntax Root)
{
    public string Path => File.Path;

    /// <summary>1-based line of a position.</summary>
    public int LineOf(int position) => Root.SyntaxTree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0)).StartLinePosition.Line + 1;
}

/// <summary>A declaration of a type: one part of a partial class or the whole class.</summary>
/// <param name="FullName">As <c>Type.FullName</c> writes it: namespace, nested types with <c>+</c>, generic with <c>`1</c>.</param>
public sealed record TypeSite(ScannedFile File, TypeDeclarationSyntax Declaration, string FullName)
{
    public string Namespace => FullName.LastIndexOf('.') is var dot and > 0 && !FullName[..dot].Contains('+', StringComparison.Ordinal) ? FullName[..dot] : "";
}

/// <summary>Where an entity is configured (WP-41).</summary>
public enum ConfigSiteKind
{
    /// <summary><c>Configure(EntityTypeBuilder&lt;T&gt; builder)</c> of an <c>IEntityTypeConfiguration&lt;T&gt;</c>.</summary>
    Class,

    /// <summary><c>modelBuilder.Entity&lt;T&gt;(b =&gt; { … })</c>.</summary>
    Lambda,

    /// <summary>Statements <c>modelBuilder.Entity&lt;T&gt;().Property(…)…;</c> in <c>OnModelCreating</c>.</summary>
    Chain,
}

/// <summary>
/// A place that configures an entity: the block its statements stand in and how they start – the builder parameter
/// (<c>builder</c>, <c>b</c>) or the call that names the entity (<c>modelBuilder.Entity&lt;Kunde&gt;()</c>).
/// </summary>
/// <param name="Statements">The statements of this entity in <paramref name="Block"/>, in order.</param>
/// <param name="ClassName">The configuration class (<see cref="ConfigSiteKind.Class"/>); null otherwise.</param>
public sealed record ConfigSite(ScannedFile File, ConfigSiteKind Kind, BlockSyntax Block, string Prefix, IReadOnlyList<ExpressionStatementSyntax> Statements, string? ClassName);

/// <summary>
/// The C# files of a work clone that name what a change needs (WP-41, ADR 0050), parsed with Roslyn – syntax only, no
/// compilation: the user's build may be broken, and a workspace of the solution takes minutes. Files are found by the
/// identifiers they hold, so a type is found whatever its file is called (decision of the user: the index of WP-40 is only a
/// hint). Folders <c>bin</c>, <c>obj</c>, dot folders and links (junctions, symbolic links) are skipped: a link could lead
/// out of the work clone, and FerretSharp writes only below it.
/// </summary>
public sealed class CodeScan
{
    private static readonly CSharpParseOptions Options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private const string ConfigurationInterface = "IEntityTypeConfiguration";

    private readonly Lazy<IReadOnlyList<TypeSite>> _types;

    private CodeScan(string root, IReadOnlyList<ScannedFile> files, IReadOnlyList<string> unreadable)
    {
        Root = root;
        Files = files;
        Unreadable = unreadable;
        _types = new Lazy<IReadOnlyList<TypeSite>>(() => [.. Files.SelectMany(f => f.Root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(t => new TypeSite(f, t, FullName(t))))]);
    }

    public string Root { get; }

    public IReadOnlyList<ScannedFile> Files { get; }

    /// <summary>Files holding one of the words that could not be read (too large, not UTF-8/UTF-16), relative to <see cref="Root"/>.</summary>
    public IReadOnlyList<string> Unreadable { get; }

    /// <summary>Every type declared in the scanned files.</summary>
    public IReadOnlyList<TypeSite> Types => _types.Value;

    /// <summary>
    /// The <c>.cs</c> files below <paramref name="root"/> that hold one of <paramref name="words"/> as an identifier, and
    /// those with <c>global using</c> (they decide which names a file sees).
    /// </summary>
    public static CodeScan Read(string root, IReadOnlyCollection<string> words, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(words, StringComparer.Ordinal);
        var files = new List<ScannedFile>();
        var unreadable = new List<string>();
        foreach (var path in SourceFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodeFile? file;
            try
            {
                file = CodeFile.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // locked or gone
            }

            var relative = System.IO.Path.GetRelativePath(root, path);
            if (file is null)
            {
                unreadable.Add(relative);
                continue;
            }

            if (!HoldsWord(file.Text, wanted) && !file.Text.Contains("global using", StringComparison.Ordinal))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(file.Text, Options, path, cancellationToken: cancellationToken);
            files.Add(new ScannedFile(file, relative, (CompilationUnitSyntax)tree.GetRoot(cancellationToken)));
        }

        return new CodeScan(root, files, unreadable);
    }

    /// <summary>For tests: the scan of files given as text.</summary>
    internal static CodeScan Of(string root, IEnumerable<(string Relative, string Text)> files) =>
        new(root, [.. files.Select(f =>
        {
            var path = System.IO.Path.Combine(root, f.Relative);
            var file = CodeFile.FromBytes(path, System.Text.Encoding.UTF8.GetBytes(f.Text))!;
            return new ScannedFile(file, f.Relative, (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(f.Text, Options, path).GetRoot());
        })], []);

    /// <summary>Text parsed as these files are, for checking a change (<see cref="SyntaxErrors"/>).</summary>
    internal static CompilationUnitSyntax Parse(string text) => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, Options).GetRoot();

    /// <summary>The syntax errors of a text, as messages with their line.</summary>
    internal static IReadOnlyList<string> SyntaxErrors(CompilationUnitSyntax root) =>
        [.. root.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")];

    /// <summary>
    /// The problem with declarations of one full name that are not all <c>partial</c>: the same class in several projects of
    /// the clone (an old copy, a tool) – which one the model means is not known (review WP-41: both were changed).
    /// </summary>
    internal static string? Ambiguous(IReadOnlyList<TypeSite> parts) =>
        parts.Count > 1 && parts.Any(p => !p.Declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            ? Resources.TextFormat.Format(Resources.CodeEditText.Problem_Ambiguous, parts[0].FullName, string.Join(", ", parts.Select(p => p.File.Relative).Distinct()))
            : null;

    /// <summary>The declarations (all parts) of the type with this full name.</summary>
    public IReadOnlyList<TypeSite> TypesNamed(string fullName) => [.. Types.Where(t => t.FullName == fullName)];

    /// <summary>
    /// The places configuring the entity: configuration classes, <c>Entity&lt;T&gt;(b =&gt; …)</c> and
    /// <c>Entity&lt;T&gt;()…</c> statements. A type name counts if it is written qualified as the entity, or unqualified where
    /// the file sees the entity's namespace (its own namespace or a parent, a <c>using</c>, a <c>global using</c>) – or if no
    /// other type of the scan has that name.
    /// </summary>
    public IReadOnlyList<ConfigSite> ConfigurationsOf(string entityFullName)
    {
        var simple = ClrModelMapping.ShortName(entityFullName);
        var sites = new List<ConfigSite>();
        foreach (var file in Files)
        {
            if (!file.File.Text.Contains(simple, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var type in file.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (type.BaseList?.Types.Any(b => ConfigurationOf(b.Type) is { } entity && Names(entity, entityFullName, file)) != true)
                {
                    continue;
                }

                var configure = type.Members.OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == "Configure" && m.ParameterList.Parameters.Count == 1 && m.Body is not null);
                if (configure?.Body is { } body)
                {
                    var builder = configure.ParameterList.Parameters[0].Identifier.ValueText;
                    sites.Add(new ConfigSite(file, ConfigSiteKind.Class, body, builder, Own(body, builder), type.Identifier.ValueText));
                }
            }

            foreach (var call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (EntityCall(call) is not { } entity || !Names(entity, entityFullName, file))
                {
                    continue;
                }

                if (call.ArgumentList.Arguments is [{ Expression: LambdaExpressionSyntax { Block: { } block } lambda }])
                {
                    var parameter = lambda switch
                    {
                        SimpleLambdaExpressionSyntax s => s.Parameter.Identifier.ValueText,
                        ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var p] } => p.Identifier.ValueText,
                        _ => null,
                    };
                    if (parameter is not null)
                    {
                        sites.Add(new ConfigSite(file, ConfigSiteKind.Lambda, block, parameter, Own(block, parameter), null));
                    }
                }
                else if (call.ArgumentList.Arguments.Count == 0 && call.Parent is MemberAccessExpressionSyntax
                         && call.FirstAncestorOrSelf<ExpressionStatementSyntax>() is { Parent: BlockSyntax method } statement
                         && Receiver(statement.Expression) == call.ToString()
                         && !sites.Any(s => s.Block == method && s.Kind == ConfigSiteKind.Chain && s.Prefix == call.ToString()))
                {
                    var prefix = call.ToString();
                    sites.Add(new ConfigSite(file, ConfigSiteKind.Chain, method, prefix, Own(method, prefix), null));
                }
            }
        }

        return sites;
    }

    /// <summary>The entity type a base type names, if it is <c>IEntityTypeConfiguration&lt;T&gt;</c>.</summary>
    private static TypeSyntax? ConfigurationOf(TypeSyntax type) => Rightmost(type) is GenericNameSyntax { Identifier.ValueText: ConfigurationInterface, TypeArgumentList.Arguments: [var entity] }
        ? entity
        : null;

    /// <summary>The entity type of <c>….Entity&lt;T&gt;(…)</c>.</summary>
    private static TypeSyntax? EntityCall(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Entity", TypeArgumentList.Arguments: [var entity] } }
            ? entity
            : null;

    /// <summary>The statements of a block that start with <paramref name="prefix"/> (<c>builder.…</c>).</summary>
    private static List<ExpressionStatementSyntax> Own(BlockSyntax block, string prefix) =>
        [.. block.Statements.OfType<ExpressionStatementSyntax>().Where(s => Receiver(s.Expression) == prefix)];

    /// <summary>
    /// The start of a call chain: <c>builder</c> for <c>builder.Property(x =&gt; x.A).HasMaxLength(5)</c>,
    /// <c>modelBuilder.Entity&lt;T&gt;()</c> for a chain on that call.
    /// </summary>
    internal static string? Receiver(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation when EntityCall(invocation) is not null && invocation.ArgumentList.Arguments.Count == 0:
                    return invocation.ToString();
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                case MemberAccessExpressionSyntax access:
                    current = access.Expression;
                    break;
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText;
                case ThisExpressionSyntax:
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>Whether <paramref name="type"/>, written in <paramref name="file"/>, names the type <paramref name="fullName"/>.</summary>
    internal bool Names(TypeSyntax type, string fullName, ScannedFile file)
    {
        var simple = ClrModelMapping.ShortName(fullName);
        var written = type is AliasQualifiedNameSyntax alias ? alias.Name : type;
        if (Rightmost(written) is not SimpleNameSyntax { Identifier.ValueText: var name } || name != simple)
        {
            return false;
        }

        var dot = fullName.Replace('+', '.');
        var text = written.ToString().Replace(" ", "", StringComparison.Ordinal);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            return dot == text || dot.EndsWith("." + text, StringComparison.Ordinal);
        }

        var ns = fullName.LastIndexOf('.') is var at and > 0 ? fullName[..at] : "";
        // C# looks in the enclosing namespaces before the usings: a type of that name there is the one meant.
        var enclosing = Enclosing(type);
        var nearest = Types.Where(t => ClrModelMapping.ShortName(t.FullName) == simple && !t.FullName.Contains('+', StringComparison.Ordinal))
            .Select(t => t.Namespace).Where(enclosing.Contains).OrderByDescending(n => n.Length).FirstOrDefault();
        if (nearest is not null)
        {
            return nearest == ns;
        }

        return Sees(file, type, ns) || Types.Count(t => ClrModelMapping.ShortName(t.FullName) == simple) <= 1;
    }

    /// <summary>The namespace a node stands in and all its parents, down to the global one (<c>""</c>).</summary>
    private static HashSet<string> Enclosing(SyntaxNode node)
    {
        var name = string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString().Replace(" ", "", StringComparison.Ordinal)));
        var result = new HashSet<string>(StringComparer.Ordinal) { "" };
        for (var at = name.Length; at > 0; at = name.LastIndexOf('.', at - 1) is var dot and >= 0 ? dot : 0)
        {
            result.Add(name[..at]);
        }

        return result;
    }

    /// <summary>The file sees the names of <paramref name="ns"/> at <paramref name="node"/>: its namespace or a parent, or a using.</summary>
    internal bool Sees(ScannedFile file, SyntaxNode node, string ns)
    {
        if (ns.Length == 0)
        {
            return true;
        }

        var enclosing = string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
        if (enclosing == ns || enclosing.StartsWith(ns + ".", StringComparison.Ordinal))
        {
            return true;
        }

        var usings = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings).Concat(file.Root.Usings)
            .Concat(Files.SelectMany(f => f.Root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))));
        return usings.Any(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.NamespaceOrType.ToString().Replace(" ", "", StringComparison.Ordinal) == ns);
    }

    private static TypeSyntax Rightmost(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right,
        AliasQualifiedNameSyntax alias => alias.Name,
        _ => type,
    };

    /// <summary>The full name as <c>Type.FullName</c> writes it.</summary>
    internal static string FullName(BaseTypeDeclarationSyntax type)
    {
        var names = new List<string>();
        for (SyntaxNode? node = type; node is not null; node = node.Parent)
        {
            if (node is BaseTypeDeclarationSyntax t)
            {
                var arity = t is TypeDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 and var n } ? $"`{n}" : "";
                names.Insert(0, t.Identifier.ValueText + arity);
            }
        }

        var ns = string.Join('.', type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString().Replace(" ", "", StringComparison.Ordinal)));
        var nested = string.Join('+', names);
        return ns.Length == 0 ? nested : $"{ns}.{nested}";
    }

    /// <summary>Whether the text holds one of the words as an identifier (letters, digits, <c>_</c>).</summary>
    private static bool HoldsWord(string text, HashSet<string> words)
    {
        var lookup = words.GetAlternateLookup<ReadOnlySpan<char>>();
        var span = text.AsSpan();
        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            var part = i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] == '_');
            if (part && start < 0)
            {
                start = i;
            }
            else if (!part && start >= 0)
            {
                if (lookup.Contains(span[start..i]))
                {
                    return true;
                }

                start = -1;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>.cs</c> files below the root in ordinal order; <c>bin</c>, <c>obj</c>, dot folders and links skipped (a
    /// junction in the work clone could lead anywhere – only files really below it are read and written).
    /// </summary>
    internal static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory, "*.cs");
                directories = Directory.GetDirectories(directory);
                Array.Sort(files, StringComparer.Ordinal);
                Array.Sort(directories, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsLink(file))
                {
                    yield return file;
                }
            }

            for (var i = directories.Length - 1; i >= 0; i--)
            {
                var child = directories[i];
                var name = System.IO.Path.GetFileName(child);
                if (!name.StartsWith('.') && !name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase) && !IsLink(child))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>A junction or symbolic link – or a path that cannot be looked at (not OneDrive placeholders, review WP-41).</summary>
    internal static bool IsLink(string path) => FerretSharp.Core.IO.UserFolderPaths.IsLink(path);
}
