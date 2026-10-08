using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FerretSharp.Core.ClrModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FerretSharp.ModelHost;

/// <summary>
/// Reads the EF Core model of a user's project (ADR 0009) or serves the LINQ console (ADR 0011). Started by FerretSharp
/// with <c>dotnet exec</c> and the project's deps.json, so the project's runtime, EF Core and provider are used.
/// <code>FerretSharp.ModelHost --assembly &lt;Data.dll&gt; --output &lt;result.json&gt; [--context &lt;type name&gt;] [--culture &lt;de-DE&gt;]</code>
/// writes a <see cref="ModelHostResult"/> as JSON to <c>--output</c> (not stdout: the project's code may write to the
/// console); exit code 0 with a model, 1 with an error in the result, 2 if not even the result could be written.
/// <code>FerretSharp.ModelHost --assembly &lt;Data.dll&gt; --console &lt;pipe name&gt; [--context …] [--culture …]</code>
/// connects to FerretSharp's named pipe and answers <see cref="LinqRequest"/>s until told to shut down.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Ends the process with the exit code even if the project's code left foreground threads running (a timer, a
    /// background worker): FerretSharp would otherwise wait for its timeout and discard the result already written.
    /// </summary>
    private static void Main(string[] args) => Environment.Exit(Run(args));

    private static int Run(string[] args)
    {
        var options = Arguments.Parse(args);
        // Display names of enum members come from the project's resources in this culture (FerretSharp's UI language).
        if (options.Culture is { Length: > 0 } culture)
        {
            try
            {
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            }
            catch (CultureNotFoundException)
            {
                // unknown culture: keep the system's
            }
        }

        if (options.Console is { } pipe)
        {
            return LinqConsole.Serve(pipe, () => LoadContextType(options));
        }

        if (options.Output is null)
        {
            Console.Error.WriteLine("--output fehlt.");
            return 2;
        }

        ModelHostResult result;
        try
        {
            result = ReadModel(options);
        }
        catch (ModelHostException ex)
        {
            result = new ModelHostResult(null, ex.Error);
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

    private static ModelHostResult ReadModel(Arguments options)
    {
        var (contextType, types) = LoadContextType(options);
        Step("Erzeuge den DbContext");
        var (context, createdBy) = CreateContext(contextType, types);
        using (context)
        {
            Step("Baue das Modell (OnModelCreating)");
            var model = BuildModel(context, contextType);
            Step("Lese das Modell aus");
            return new ModelHostResult(ModelReader.Read(model, contextType, createdBy), null);
        }
    }

    /// <summary>Loads the project's assembly and finds its DbContext type.</summary>
    /// <exception cref="ModelHostException">Nothing to load, no or several contexts.</exception>
    internal static (Type ContextType, Type[] Types) LoadContextType(Arguments options)
    {
        if (options.Assembly is null || !File.Exists(options.Assembly))
        {
            throw Error(ModelHostErrorKind.Arguments, $"Assembly nicht gefunden: {options.Assembly ?? "(--assembly fehlt)"}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Assembly))!;
        // Project references (e.g. the entities) lie next to the assembly; NuGet packages come from the deps.json. What the
        // host brings itself (Roslyn for the LINQ console) lies next to the host, which the project's deps.json does not know.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var folder in new[] { directory, HostDirectory })
            {
                // Satellite assemblies (resources of a culture) lie in a folder named after it: de\X.resources.dll.
                var candidate = string.IsNullOrEmpty(name.CultureName)
                    ? Path.Combine(folder, name.Name + ".dll")
                    : Path.Combine(folder, name.CultureName, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return context.LoadFromAssemblyPath(candidate);
                }
            }

            return null;
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
            throw Error(ModelHostErrorKind.AssemblyNotLoadable, $"{Path.GetFileName(options.Assembly)} lässt sich nicht laden: {ex.Message}", ex);
        }

        var contexts = types.Where(t => typeof(DbContext).IsAssignableFrom(t) && t is { IsAbstract: false, IsGenericTypeDefinition: false }).ToList();
        if (options.Context is { } wanted)
        {
            contexts = contexts.Where(t => t.FullName == wanted || t.Name == wanted).ToList();
        }

        if (contexts.Count == 0)
        {
            throw Error(ModelHostErrorKind.NoContext, options.Context is null
                ? $"In {assembly.GetName().Name} gibt es keinen DbContext."
                : $"In {assembly.GetName().Name} gibt es keinen DbContext „{options.Context}“.");
        }

        if (contexts.Count > 1)
        {
            throw Error(ModelHostErrorKind.SeveralContexts,
                $"Mehrere DbContexts – bitte einen angeben: {string.Join(", ", contexts.Select(t => t.FullName))}");
        }

        return (contexts[0], types);
    }

    /// <exception cref="ModelHostException">The context could not be created.</exception>
    internal static (DbContext Context, string CreatedBy) CreateContext(Type contextType, Type[] types, Action<DbContextOptionsBuilder>? configure = null)
    {
        try
        {
            return ContextFactory.Create(contextType, types, configure);
        }
        catch (ModelHostException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var inner = Unwrap(ex);
            throw Error(ModelHostErrorKind.ContextCreationFailed, $"{contextType.Name} ließ sich nicht erzeugen: {inner.Message}", inner);
        }
    }

    /// <summary>Builds the model: OnModelCreating, conventions, configurations – no database connection.</summary>
    /// <exception cref="ModelHostException">The project's model building failed.</exception>
    internal static Microsoft.EntityFrameworkCore.Metadata.IModel BuildModel(DbContext context, Type contextType)
    {
        try
        {
            return context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        }
        catch (Exception ex)
        {
            var inner = Unwrap(ex);
            throw Error(ModelHostErrorKind.ModelBuildFailed, $"Das Modell von {contextType.Name} ließ sich nicht bauen: {inner.Message}", inner);
        }
    }

    /// <summary>Starts a step: reported to FerretSharp as a progress line on stdout (FerretSharp measures the durations).</summary>
    internal static void Step(string name)
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

    /// <summary>The host's own folder (with <c>dotnet exec --depsfile</c>, AppContext.BaseDirectory is not reliably it).</summary>
    private static readonly string HostDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;

    internal static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;

    internal static ModelHostResult Fail(string kind, string message, Exception? ex = null) =>
        new(null, new ModelHostError(kind, message, ex?.ToString()));

    private static ModelHostException Error(string kind, string message, Exception? ex = null) =>
        new(new ModelHostError(kind, message, ex?.ToString()));

    internal sealed record Arguments(string? Assembly, string? Output, string? Context, string? Culture, string? Console)
    {
        public static Arguments Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            return new Arguments(Value("--assembly"), Value("--output"), Value("--context"), Value("--culture"), Value("--console"));
        }
    }
}

/// <summary>A failure with a message for the user, reported as the result's error.</summary>
internal sealed class ModelHostException(ModelHostError error) : Exception(error.Message)
{
    public ModelHostError Error { get; } = error;
}
