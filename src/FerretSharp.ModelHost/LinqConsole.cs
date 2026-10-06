using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FerretSharp.Core.ClrModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;

namespace FerretSharp.ModelHost;

/// <summary>Globals of a console script; the double underscore keeps them out of the user's way.</summary>
public sealed class LinqGlobals
{
    public DbContext __Context { get; init; } = null!;

    public CancellationToken __Token { get; init; }
}

/// <summary>
/// The LINQ console (ADR 0011): runs the user's C# against their DbContext with Roslyn scripting. EF never reaches a
/// database – <see cref="CommandCapture"/> suppresses the connection and records every command – so the result is the
/// SQL the project would send. One request at a time, over a named pipe (stdout belongs to the project's code).
/// </summary>
internal sealed class LinqConsole
{
    /// <summary>Names a copied query typically uses for its context or cancellation token.</summary>
    private static readonly HashSet<string> ContextNames = new(StringComparer.Ordinal)
        { "context", "_context", "Context", "db", "_db", "dbContext", "_dbContext", "DbContext", "ctx", "_ctx", "dataContext", "_dataContext" };

    private static readonly HashSet<string> TokenNames = new(StringComparer.Ordinal)
        { "ct", "cancellationToken", "token", "cancel", "cancellation", "_ct", "_cancellationToken" };

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private readonly Type _contextType;
    private readonly Type[] _types;
    private readonly CommandCapture _capture = new();
    private readonly ScriptOptions _options;
    private readonly HashSet<string> _contextMembers;
    private readonly HashSet<string> _projectNamespaces;

    private LinqConsole(Type contextType, Type[] types)
    {
        _contextType = contextType;
        _types = types;
        _contextMembers = contextType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        // One context up front: builds the model (and fails early if it cannot), and loads the assemblies scripts refer to.
        using var context = Create();
        var model = Program.BuildModel(context, contextType);
        var namespaces = model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType).Append(e.ClrType))
            .Select(t => t.Namespace)
            .Append(contextType.Namespace)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _projectNamespaces = namespaces.Where(n => !n.StartsWith("System", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        _options = ScriptOptions.Default
            .AddReferences(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0))
            .AddImports(["System", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "Microsoft.EntityFrameworkCore", .. namespaces])
            .WithOptimizationLevel(OptimizationLevel.Debug);
    }

    /// <summary>Connects to FerretSharp's pipe, loads the project and answers requests until shutdown or disconnect.</summary>
    public static int Serve(string pipeName, Func<(Type ContextType, Type[] Types)> load)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(TimeSpan.FromSeconds(30));
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

        void Send(LinqResponse response) => writer.WriteLine(JsonSerializer.Serialize(response, LinqProtocol.JsonOptions));

        LinqConsole console;
        try
        {
            var (contextType, types) = load();
            Program.Step("Baue das Modell (OnModelCreating)");
            console = new LinqConsole(contextType, types);
        }
        catch (ModelHostException ex)
        {
            Send(new LinqResponse(0, Error: ex.Error));
            return 1;
        }
        catch (Exception ex)
        {
            Send(new LinqResponse(0, Error: new ModelHostError(ModelHostErrorKind.Unexpected, ex.Message, ex.ToString())));
            return 1;
        }

        Send(new LinqResponse(0));
        while (reader.ReadLine() is { } line)
        {
            var request = JsonSerializer.Deserialize<LinqRequest>(line, LinqProtocol.JsonOptions);
            if (request is null || request.Kind == LinqProtocol.Shutdown)
            {
                break;
            }

            try
            {
                Send(request.Kind == LinqProtocol.Complete
                    ? new LinqResponse(request.Id, Completion: console.Complete(request.Code ?? "", request.Variables ?? "", request.Section, request.Offset))
                    : new LinqResponse(request.Id, console.Run(request.Code ?? "", request.Variables ?? "")));
            }
            catch (Exception ex)
            {
                Send(new LinqResponse(request.Id, Error: new ModelHostError(ModelHostErrorKind.Unexpected, ex.Message, ex.ToString())));
            }
        }

        return 0;
    }

    private DbContext Create() => Program.CreateContext(_contextType, _types, builder => builder.AddInterceptors(_capture, _capture.Connections)).Context;

    /// <summary>
    /// Compiles variables and code; names nobody declares are either the context or a token (declared automatically) or
    /// become suggestions (then nothing runs). Otherwise runs the code with a fresh context and returns the captured commands.
    /// </summary>
    internal LinqRunResult Run(string code, string variables)
    {
        var watch = Stopwatch.StartNew();
        var first = Analyze(code, variables, prelude: []);
        var unknown = first.Diagnostics.Where(d => d.Id == "CS0103").Select(d => NameAt(first.Tree, d)).Distinct(StringComparer.Ordinal).ToList();

        var prelude = new List<string>();
        var auto = new List<string>();
        foreach (var name in unknown)
        {
            if (IsContext(first.Tree, name))
            {
                prelude.Add(ContextDeclaration(name));
                auto.Add(name);
            }
            else if (TokenNames.Contains(name))
            {
                prelude.Add($"var {name} = __Token;");
                auto.Add(name);
            }
        }

        // With the context declared, the lambdas have types again: only now can the other names be typed from their use.
        var analysis = prelude.Count == 0 ? first : Analyze(code, variables, prelude);
        var suggestions = analysis.Diagnostics.Where(d => d.Id == "CS0103")
            .Select(d => NameAt(analysis.Tree, d))
            .Distinct(StringComparer.Ordinal)
            .Select(name => TypeSuggestion.For(analysis, name))
            .ToList();
        if (suggestions.Count > 0)
        {
            var others = analysis.Diagnostics.Where(d => d.Id != "CS0103").Select(d => Map(d, analysis.Layout)).OfType<LinqDiagnostic>().ToList();
            return new LinqRunResult(others, suggestions, auto, [], null, null, watch.Elapsed);
        }

        var diagnostics = analysis.Diagnostics.Select(d => Map(d, analysis.Layout)).OfType<LinqDiagnostic>().ToList();
        if (diagnostics.Any(d => d.Severity == "error"))
        {
            return new LinqRunResult(diagnostics, [], auto, [], null, null, watch.Elapsed);
        }

        using var timeout = new CancellationTokenSource(RunTimeout);
        using var context = Create();
        _capture.Start();
        string? resultType = null;
        string? exception = null;
        try
        {
            var state = analysis.Script.RunAsync(new LinqGlobals { __Context = context, __Token = timeout.Token }, _ => true, timeout.Token)
                .GetAwaiter().GetResult();
            if (state.Exception is { } thrown)
            {
                exception = Describe(thrown);
            }
            else
            {
                (resultType, exception) = Finish(state.ReturnValue);
            }
        }
        catch (Exception ex)
        {
            exception = Describe(ex);
        }

        return new LinqRunResult(diagnostics, [], auto, _capture.Stop(), resultType, exception, watch.Elapsed);
    }

    /// <summary>
    /// Completion at the cursor (WP-19): the script as for a run – the context and a token declared where the code uses
    /// them undeclared, so <c>_context.</c> in copied code offers the DbSets – and the semantic model asked there.
    /// </summary>
    /// <param name="section">The section the cursor is in; the other one is part of the script around it.</param>
    /// <param name="offset">The cursor in that section's text.</param>
    internal IReadOnlyList<LinqCompletionItem> Complete(string code, string variables, string? section, int offset)
    {
        var bare = CSharpScript.Create(ScriptText([], variables, code), _options, typeof(LinqGlobals)).GetCompilation();
        var tree = bare.SyntaxTrees.First();
        var model = bare.GetSemanticModel(tree);
        var prelude = new List<string>();
        foreach (var name in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                     .GroupBy(n => n.Identifier.Text, StringComparer.Ordinal)
                     .Where(g => model.GetSymbolInfo(g.First()) is { Symbol: null, CandidateSymbols.Length: 0 })
                     .Select(g => g.Key))
        {
            if (IsContext(tree, name))
            {
                prelude.Add(ContextDeclaration(name));
            }
            else if (TokenNames.Contains(name))
            {
                prelude.Add($"var {name} = __Token;");
            }
        }

        var text = ScriptText(prelude, variables, code);
        var compilation = prelude.Count == 0 ? bare : CSharpScript.Create(text, _options, typeof(LinqGlobals)).GetCompilation();
        var sectionText = section == LinqProtocol.VariablesSection ? variables : code;
        var start = PreludeText(prelude).Length + (section == LinqProtocol.VariablesSection ? 0 : variables.Length + 1);
        var position = start + Math.Clamp(offset, 0, sectionText.Length);
        var syntax = compilation.SyntaxTrees.First();
        return LinqCompletion.Items(compilation.GetSemanticModel(syntax), position, _projectNamespaces);
    }

    private string ContextDeclaration(string name) => $"var {name} = (global::{_contextType.FullName!.Replace('+', '.')})__Context;";

    private static string PreludeText(IReadOnlyList<string> prelude) => string.Concat(prelude.Select(p => p + "\n"));

    private static string ScriptText(IReadOnlyList<string> prelude, string variables, string code) => PreludeText(prelude) + variables + "\n" + code;

    /// <summary>
    /// What the code returned: a task is awaited; a query that was not executed (<c>IQueryable</c>) is enumerated, so EF
    /// sends – and the capture records – its command.
    /// </summary>
    private static (string? Type, string? Exception) Finish(object? value)
    {
        try
        {
            if (value is Task task)
            {
                task.GetAwaiter().GetResult();
                var resultProperty = task.GetType().GetProperty("Result");
                value = resultProperty is not null && task.GetType().IsGenericType ? resultProperty.GetValue(task) : null;
                if (value?.GetType().FullName == "System.Threading.Tasks.VoidTaskResult")
                {
                    value = null;
                }
            }

            if (value is IQueryable queryable)
            {
                var type = ModelReader.TypeName(queryable.GetType().GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>)) ?? typeof(IQueryable));
                var enumerator = ((IEnumerable)queryable).GetEnumerator();
                enumerator.MoveNext();
                (enumerator as IDisposable)?.Dispose();
                return (type, null);
            }

            return (value is null ? null : ModelReader.TypeName(value.GetType()), null);
        }
        catch (Exception ex)
        {
            return (value is null ? null : ModelReader.TypeName(value.GetType()), Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var inner = Program.Unwrap(ex is AggregateException { InnerExceptions: [var single] } ? single : ex);
        return $"{inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>A name used like the context: <c>name.DbSet</c>, <c>name.Set&lt;T&gt;()</c>, or one of the usual names.</summary>
    private bool IsContext(SyntaxTree tree, string name) =>
        ContextNames.Contains(name)
        || tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(n => n.Identifier.Text == name && n.Parent is MemberAccessExpressionSyntax access && access.Expression == n
                      && _contextMembers.Contains(access.Name.Identifier.Text));

    private Analysis Analyze(string code, string variables, IReadOnlyList<string> prelude)
    {
        var layout = new Layout(prelude.Count, variables.Split('\n').Length);
        var text = ScriptText(prelude, variables, code);
        var script = CSharpScript.Create(text, _options, typeof(LinqGlobals));
        var compilation = script.GetCompilation();
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && d.Id is not ("CS1701" or "CS1702"))
            .ToList();
        return new Analysis(script, compilation, compilation.SyntaxTrees.First(), diagnostics, layout);
    }

    private static string NameAt(SyntaxTree tree, Diagnostic diagnostic) => tree.GetText().ToString(diagnostic.Location.SourceSpan);

    /// <summary>A Roslyn diagnostic in the user's sections; null if it lies in the automatic prelude.</summary>
    private static LinqDiagnostic? Map(Diagnostic diagnostic, Layout layout)
    {
        var span = diagnostic.Location.GetLineSpan();
        var (section, line) = layout.SectionOf(span.StartLinePosition.Line);
        if (section is null)
        {
            return null;
        }

        var endLine = span.EndLinePosition.Line - (span.StartLinePosition.Line - line);
        return new LinqDiagnostic(section, line + 1, span.StartLinePosition.Character + 1, endLine + 1, span.EndLinePosition.Character + 1,
            diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning", diagnostic.Id, diagnostic.GetMessage());
    }

    internal sealed record Analysis(Script Script, Compilation Compilation, SyntaxTree Tree, IReadOnlyList<Diagnostic> Diagnostics, Layout Layout);

    /// <summary>Where the sections start in the script text: prelude lines, then the variables, then the code.</summary>
    internal sealed record Layout(int PreludeLines, int VariableLines)
    {
        /// <returns>The section and the 0-based line within it; no section for the prelude.</returns>
        public (string? Section, int Line) SectionOf(int scriptLine) =>
            scriptLine < PreludeLines ? (null, 0)
            : scriptLine < PreludeLines + VariableLines ? (LinqProtocol.VariablesSection, scriptLine - PreludeLines)
            : (LinqProtocol.CodeSection, scriptLine - PreludeLines - VariableLines);
    }
}
