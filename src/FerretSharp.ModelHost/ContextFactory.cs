using System.Reflection;
using FerretSharp.Core.ClrModel;
using FerretSharp.ModelHost.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FerretSharp.ModelHost;

/// <summary>
/// Creates the user's DbContext without running the project's start-up code (ADR 0009): its design-time factory if
/// there is one, otherwise options of our own with the Oracle provider and a placeholder connection string – building
/// the model never connects – and the constructor taking <c>DbContextOptions</c>, otherwise a parameterless one.
/// </summary>
internal static class ContextFactory
{
    /// <summary>Never opened: the model is built without a connection.</summary>
    private const string PlaceholderConnectionString = "Data Source=ferretsharp-model-only;User Id=ferretsharp;Password=none";

    private const BindingFlags Constructors = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <param name="configure">Further options (interceptors of the LINQ console); not applied to a design-time factory's context.</param>
    public static (DbContext Context, string CreatedBy) Create(Type contextType, IReadOnlyList<Type> types, Action<DbContextOptionsBuilder>? configure = null)
    {
        var factoryInterface = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(contextType);
        if (types.FirstOrDefault(t => factoryInterface.IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null) is { } factory)
        {
            var instance = Activator.CreateInstance(factory)!;
            var context = (DbContext)factoryInterface.GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!
                .Invoke(instance, [Array.Empty<string>()])!;
            return (context, "factory");
        }

        var typedOptions = typeof(DbContextOptions<>).MakeGenericType(contextType);
        var optionsConstructor = contextType.GetConstructors(Constructors)
            .FirstOrDefault(c => c.GetParameters() is [var p] && p.ParameterType.IsAssignableFrom(typedOptions));
        if (optionsConstructor is not null)
        {
            var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
            UseOracle(builder);
            configure?.Invoke(builder);
            return ((DbContext)optionsConstructor.Invoke([builder.Options]), "options");
        }

        if (contextType.GetConstructor(Constructors, Type.EmptyTypes) is { } parameterless)
        {
            return ((DbContext)parameterless.Invoke(null), "parameterless");
        }

        var signatures = contextType.GetConstructors(Constructors)
            .Select(c => $"{contextType.Name}({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))})");
        throw new ModelHostException(new ModelHostError(
            ModelHostErrorKind.ConstructorNeedsServices,
            Program.Text(ModelHostText.ConstructorNeedsServices, contextType.Name, string.Join("; ", signatures))));
    }

    /// <summary>
    /// <c>UseOracle</c> of the project's provider, by reflection: FerretSharp does not compile against a particular
    /// Oracle.EntityFrameworkCore version.
    /// </summary>
    private static void UseOracle(DbContextOptionsBuilder builder)
    {
        Assembly provider;
        try
        {
            provider = Assembly.Load(new AssemblyName("Oracle.EntityFrameworkCore"));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
        {
            throw new ModelHostException(new ModelHostError(
                ModelHostErrorKind.NoProvider, ModelHostText.NoOracleProvider, ex.ToString()));
        }

        var useOracle = provider.GetType("Microsoft.EntityFrameworkCore.OracleDbContextOptionsExtensions")?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "UseOracle" && !m.IsGenericMethodDefinition
                                 && m.GetParameters() is [var b, var c, ..]
                                 && b.ParameterType == typeof(DbContextOptionsBuilder) && c.ParameterType == typeof(string))
            ?? throw new ModelHostException(new ModelHostError(
                ModelHostErrorKind.NoProvider, Program.Text(ModelHostText.UseOracleMissing, provider.GetName())));

        var arguments = new object?[useOracle.GetParameters().Length];
        arguments[0] = builder;
        arguments[1] = PlaceholderConnectionString;
        useOracle.Invoke(null, arguments);
    }
}
