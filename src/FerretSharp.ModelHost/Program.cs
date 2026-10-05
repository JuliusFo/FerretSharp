using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FerretSharp.Core.ClrModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FerretSharp.ModelHost;

/// <summary>
/// Reads the EF Core model of a user's project (ADR 0009). Started by FerretSharp with <c>dotnet exec</c> and the
/// project's deps.json, so the project's runtime, EF Core and provider are used. Writes a <see cref="ModelHostResult"/>
/// as JSON to <c>--output</c> (not stdout: the project's code may write to the console).
/// <code>FerretSharp.ModelHost --assembly &lt;Data.dll&gt; --output &lt;result.json&gt; [--context &lt;type name&gt;]</code>
/// Exit code 0 with a model, 1 with an error in the result, 2 if not even the result could be written.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = Arguments.Parse(args);
        if (options.Output is null)
        {
            Console.Error.WriteLine("--output fehlt.");
            return 2;
        }

        ModelHostResult result;
        try
        {
            result = Run(options);
        }
        catch (Exception ex)
        {
            result = Fail(ModelHostErrorKind.Unexpected, ex.Message, ex);
        }


        try
        {
            File.WriteAllText(options.Output, JsonSerializer.Serialize(result, ModelHostResult.JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }

        return result.Model is null ? 1 : 0;
    }

    private static ModelHostResult Run(Arguments options)
    {
        if (options.Assembly is null || !File.Exists(options.Assembly))
        {
            return Fail(ModelHostErrorKind.Arguments, $"Assembly nicht gefunden: {options.Assembly ?? "(--assembly fehlt)"}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Assembly))!;
        // Project references (e.g. the entities) lie next to the assembly; NuGet packages come from the deps.json.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        Step("Lade die Assemblies");
        Assembly assembly;
        Type[] types;
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(options.Assembly));
            types = LoadableTypes(assembly);
        }
        catch (Exception ex)
        {
            return Fail(ModelHostErrorKind.AssemblyNotLoadable, $"{Path.GetFileName(options.Assembly)} lässt sich nicht laden: {ex.Message}", ex);
        }

        var contexts = types.Where(t => typeof(DbContext).IsAssignableFrom(t) && t is { IsAbstract: false, IsGenericTypeDefinition: false }).ToList();
        if (options.Context is { } wanted)
        {
            contexts = contexts.Where(t => t.FullName == wanted || t.Name == wanted).ToList();
        }

        if (contexts.Count == 0)
        {
            return Fail(ModelHostErrorKind.NoContext, options.Context is null
                ? $"In {assembly.GetName().Name} gibt es keinen DbContext."
                : $"In {assembly.GetName().Name} gibt es keinen DbContext „{options.Context}“.");
        }

        if (contexts.Count > 1)
        {
            return Fail(ModelHostErrorKind.SeveralContexts,
                $"Mehrere DbContexts – bitte einen angeben: {string.Join(", ", contexts.Select(t => t.FullName))}");
        }

        var contextType = contexts[0];
        Step("Erzeuge den DbContext");
        DbContext context;
        string createdBy;
        try
        {
            (context, createdBy) = ContextFactory.Create(contextType, types);
        }
        catch (ModelHostException ex)
        {
            return new ModelHostResult(null, ex.Error);
        }
        catch (Exception ex)
        {
            var inner = Unwrap(ex);
            return Fail(ModelHostErrorKind.ContextCreationFailed, $"{contextType.Name} ließ sich nicht erzeugen: {inner.Message}", inner);
        }

        using (context)
        {
            Microsoft.EntityFrameworkCore.Metadata.IModel model;
            Step("Baue das Modell (OnModelCreating)");
            try
            {
                // Builds the model: OnModelCreating, conventions, configurations – no database connection.
                model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                return Fail(ModelHostErrorKind.ModelBuildFailed, $"Das Modell von {contextType.Name} ließ sich nicht bauen: {inner.Message}", inner);
            }

            Step("Lese das Modell aus");
            return new ModelHostResult(ModelReader.Read(model, contextType, createdBy), null);
        }
    }

    /// <summary>Starts a step: reported to FerretSharp as a progress line on stdout (FerretSharp measures the durations).</summary>
    private static void Step(string name)
    {
        Console.Out.WriteLine(ModelHostResult.ProgressPrefix + name);
        Console.Out.Flush();
    }

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>().ToArray();
        }
    }

    internal static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;

    internal static ModelHostResult Fail(string kind, string message, Exception? ex = null) =>
        new(null, new ModelHostError(kind, message, ex?.ToString()));

    private sealed record Arguments(string? Assembly, string? Output, string? Context)
    {
        public static Arguments Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            return new Arguments(Value("--assembly"), Value("--output"), Value("--context"));
        }
    }
}

/// <summary>A failure with a message for the user, reported as the result's error.</summary>
internal sealed class ModelHostException(ModelHostError error) : Exception(error.Message)
{
    public ModelHostError Error { get; } = error;
}
