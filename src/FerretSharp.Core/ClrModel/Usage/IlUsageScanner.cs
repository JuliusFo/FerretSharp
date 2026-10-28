using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// Reads an assembly's IL without loading it (WP-40, ADR 0030): which getters and setters of types of the solution its
/// methods call, which getters its expression trees name (<c>ldtoken</c>; the property an <c>ExecuteUpdate</c> sets counts as
/// written, a condition of <c>Where</c>, <c>Any</c>, <c>First</c> … as a filter), where it calls <c>DbContext.Set&lt;T&gt;()</c> –
/// with file and line from the portable PDB. Other calls (framework, packages) are not kept.
/// </summary>
public static class IlUsageScanner
{
    private const string DbContextType = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>EF Core 7–9: <c>ExecuteUpdate(s =&gt; s.SetProperty(x =&gt; x.Email, …))</c> writes <c>Email</c>.</summary>
    private const string SetPropertyCallsType = "Microsoft.EntityFrameworkCore.Query.SetPropertyCalls`1";

    private const string TreeType = "System.Linq.Expressions.Expression";

    private const string ExpressionType = "System.Linq.Expressions.Expression`1";

    private static readonly FrozenSet<short> Loads = new[]
    {
        OpCodes.Ldloc, OpCodes.Ldloc_S, OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3,
        OpCodes.Ldarg, OpCodes.Ldarg_S, OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3,
    }.Select(o => o.Value).ToFrozenSet();

    private static readonly FrozenSet<short> Stores = new[]
    {
        OpCodes.Stloc, OpCodes.Stloc_S, OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3,
        OpCodes.Starg, OpCodes.Starg_S, OpCodes.Stfld, OpCodes.Stsfld, OpCodes.Stind_Ref, OpCodes.Ret,
    }.Select(o => o.Value).ToFrozenSet();

    /// <summary>EF's async operators: <c>FirstOrDefaultAsync</c>, <c>AnyAsync</c> …</summary>
    private const string EfQueryableType = "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";

    /// <summary>
    /// Operators whose lambda is a condition on the rows (also with the suffix <c>Async</c>). Not <c>OrderBy</c>, <c>Join</c>
    /// or <c>GroupBy</c>: a missing index costs a sort or a hash join there, not a scan for a few rows. Not <c>All</c>: it
    /// becomes <c>NOT EXISTS (… WHERE NOT …)</c>, an index on the column seldom helps.
    /// </summary>
    private static readonly FrozenSet<string> FilterOperators = FrozenSet.Create(StringComparer.Ordinal,
        "Where", "Any", "Count", "LongCount", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault");

    /// <param name="solutionAssemblies">
    /// Names of the solution's assemblies: a use counts only if its type is defined in one of them (the assembly itself
    /// always counts).
    /// </param>
    /// <exception cref="IOException">The assembly could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The assembly could not be read.</exception>
    /// <exception cref="BadImageFormatException">The file is no .NET assembly.</exception>
    public static AssemblyUses Scan(string assemblyPath, IReadOnlyCollection<string> solutionAssemblies)
    {
        // The whole image in memory, shared: the build may replace the file while (or right after) it is read.
        using var stream = OpenExisting(assemblyPath);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException(null, assemblyPath);
        }

        var reader = pe.GetMetadataReader();
        MetadataReaderProvider? pdbProvider = null;
        try
        {
            pe.TryOpenAssociatedPortablePdb(assemblyPath, OpenShared, out pdbProvider, out _);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // A Windows PDB or a broken one: methods without lines.
        }

        using (pdbProvider)
        {
            var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
            var solution = new HashSet<string>(solutionAssemblies, StringComparer.OrdinalIgnoreCase) { assemblyName };
            var scan = new Scanner(reader, pdbProvider?.GetMetadataReader(), assemblyName, solution);
            scan.Run(pe);
            return new AssemblyUses(
                AssemblyUses.CurrentFormatVersion,
                reader.GetGuid(reader.GetModuleDefinition().Mvid),
                assemblyName,
                solution.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                IsTest(reader),
                pdbProvider is not null,
                scan.Types,
                scan.Uses.ToList());
        }
    }

    /// <summary>The MVID alone, to find a cached result without scanning.</summary>
    /// <exception cref="IOException">The assembly could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The assembly could not be read.</exception>
    /// <exception cref="BadImageFormatException">The file is no .NET assembly.</exception>
    public static Guid ReadMvid(string assemblyPath)
    {
        using var stream = OpenExisting(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }

    /// <summary>A running build may delete the file between finding and reading it (review of WP-40): an IOException, not null.</summary>
    private static FileStream OpenExisting(string path) =>
        OpenShared(path) ?? throw new FileNotFoundException(null, path);

    private static FileStream? OpenShared(string path) =>
        File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;

    private static bool IsTest(MetadataReader reader) =>
        reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .Any(TestFrameworks.IsTestAssembly);

    private sealed class Scanner(MetadataReader reader, MetadataReader? pdb, string assemblyName, HashSet<string> solution)
    {
        private readonly TypeNames _names = new(reader, assemblyName);

        public List<TypeLink> Types { get; } = [];

        public HashSet<CodeUse> Uses { get; } = [];

        public void Run(PEReader pe)
        {
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                // Lambdas, closures, state machines, <Module> and what the compiler adds (<>z__…): no type the developer wrote.
                var typeName = _names.Of(typeHandle).Name;
                if (!typeName.Contains('<', StringComparison.Ordinal))
                {
                    Types.Add(new TypeLink(
                        typeName,
                        type.BaseType.IsNil ? null : _names.Of(type.BaseType).Name,
                        type.GetInterfaceImplementations()
                            .Select(i => _names.Of(reader.GetInterfaceImplementation(i).Interface).Name)
                            .ToList()));
                }

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress != 0)
                    {
                        ScanBody(pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent(), typeHandle, methodHandle);
                    }
                }
            }
        }

        private void ScanBody(ImmutableArray<byte> il, TypeDefinitionHandle typeHandle, MethodDefinitionHandle methodHandle)
        {
            Caller? caller = null;
            var points = (ImmutableArray<SequencePoint>?)null;
            // ExecuteUpdate(s => s.SetProperty(x => x.Email, …)) is an expression tree: the compiler names SetProperty
            // (ldtoken) before it builds the arguments, so the next getter named is the property written.
            var selectorNext = false;
            // Getters an expression tree names wait for the call that takes the tree: Where(c => c.Email == x) filters by
            // Email, Select(c => c.Email) does not. The tree is built before the call, its nested lambdas name their
            // methods by ldtoken, so the first real call taking an Expression<T> is the one.
            var pending = new List<CodeUse>();
            // The tree built so far ends in a value, not in the lambda's parameter: in c => c.No == customer.No the compiler
            // emits Expression.Field(Expression.Constant(closure), customer) before get_No, while c.No starts with ldloc of
            // the parameter. A value's getters (customer.No, this.current.Id, request.Customer.Name) are not filtered by.
            var onValue = false;
            var treeBuilt = false;
            var offset = 0;
            while (offset < il.Length)
            {
                var start = offset;
                OpCode opCode;
                if (il[offset] == 0xFE && offset + 1 < il.Length)
                {
                    opCode = IlOpCodes.TwoByte[il[offset + 1]];
                    offset += 2;
                }
                else
                {
                    opCode = IlOpCodes.OneByte[il[offset]];
                    offset += 1;
                }

                // A tree kept in a variable, a field or returned: Where(c => c.Status == s).OrderBy(key) must not take the
                // getters of key built before. A nested lambda goes into an array (stelem), it stays with the outer tree.
                if (treeBuilt && IsStore(opCode))
                {
                    Flush(pending, CodeUseKind.Expression);
                }

                var afterTree = treeBuilt;
                treeBuilt = false;
                if (IsLoad(opCode))
                {
                    onValue = false;
                }
                else if (opCode == OpCodes.Ldnull)
                {
                    onValue = true;
                }

                if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineTok)
                {
                    var token = BitConverter.ToInt32(il.AsSpan(offset, 4));
                    var isToken = opCode == OpCodes.Ldtoken;
                    var method = Method(MetadataTokens.EntityHandle(token));
                    var isSelector = false;
                    if (isToken && method is { } named)
                    {
                        isSelector = selectorNext && named.Name.StartsWith("get_", StringComparison.Ordinal);
                        selectorNext = named is { Name: "SetProperty", Declaring.Name: SetPropertyCallsType } || (selectorNext && !isSelector);
                    }

                    if (method is { } called && Use(called, token, isToken) is { } found)
                    {
                        caller ??= Callers.Of(reader, typeHandle, methodHandle, _names);
                        points ??= SequencePoints(methodHandle);
                        var (document, line) = Locate(points.Value, start);
                        var use = new CodeUse(found.Type, found.Member, isSelector ? CodeUseKind.Write : found.Kind, caller.Type, caller.Method, document, line);
                        if (use.Kind == CodeUseKind.Expression && !onValue)
                        {
                            pending.Add(use);
                        }
                        else
                        {
                            Uses.Add(use);
                        }
                    }

                    if (opCode.FlowControl == FlowControl.Call && method is { Declaring.Name: TreeType } building)
                    {
                        // A member or a conversion keeps what its operand was: request.Customer.Name stays on the value,
                        // c.Customer.Name on the parameter. Anything else (a call, a comparison) starts anew.
                        onValue = building.Name == "Constant" || (onValue && building.Name is "Property" or "Field" or "Convert" or "ConvertChecked" or "TypeAs");
                        treeBuilt = building.Name == "Lambda";
                    }
                    else if (pending.Count > 0 && opCode.FlowControl == FlowControl.Call && method is { } taker && !BuildsTrees(taker)
                        && Signature(MetadataTokens.EntityHandle(token)) is { } signature)
                    {
                        if (TakesTree(signature))
                        {
                            Flush(pending, IsFilter(taker) ? CodeUseKind.Filter : CodeUseKind.Expression);
                        }
                        else if (afterTree && (signature.ParameterTypes.Length > 0 || signature.Header.IsInstance))
                        {
                            // The call right after the lambda takes the tree as its last argument, but not as an
                            // Expression<T>: a dictionary of sort keys, List.Add. CancellationToken.None takes nothing.
                            Flush(pending, CodeUseKind.Expression);
                        }
                    }
                }

                offset += IlOpCodes.OperandSize(opCode.OperandType, il, offset);
            }

            // No call took the tree (kept in a way not seen above): who uses it is not known here.
            Flush(pending, CodeUseKind.Expression);
        }

        private void Flush(List<CodeUse> pending, CodeUseKind kind)
        {
            foreach (var use in pending)
            {
                Uses.Add(use with { Kind = kind });
            }

            pending.Clear();
        }

        /// <summary>Building the tree itself: <c>Expression.Property</c>, <c>MethodBase.GetMethodFromHandle</c>, <c>Array.Empty</c> …</summary>
        private static bool BuildsTrees(MethodName method) =>
            method.Declaring.Name is TreeType or "System.Reflection.MethodBase" or "System.Reflection.FieldInfo" or "System.Type" or "System.Array";

        /// <summary>A local or an argument: in a tree, the lambda's parameter (or the closure a value comes from).</summary>
        private static bool IsLoad(OpCode opCode) => Loads.Contains(opCode.Value);

        /// <summary>The value just built goes into a local, an argument, a field, through a reference or is returned.</summary>
        private static bool IsStore(OpCode opCode) => Stores.Contains(opCode.Value);

        /// <summary>LINQ's and EF's operators whose lambda is a condition on the rows: <c>Where</c>, <c>Any</c>, <c>FirstOrDefaultAsync</c> …</summary>
        private static bool IsFilter(MethodName method)
        {
            if (method.Declaring.Name is not ("System.Linq.Queryable" or EfQueryableType))
            {
                return false;
            }

            var name = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name;
            return FilterOperators.Contains(name);
        }

        /// <summary>The called method's signature (a generic one by its definition); null for anything else.</summary>
        private MethodSignature<TypeName>? Signature(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            }

            return handle.Kind switch
            {
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)handle).DecodeSignature(_names, null),
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)handle).DecodeMethodSignature(_names, null),
                _ => null,
            };
        }

        /// <summary>
        /// A parameter of the called method is an <c>Expression&lt;T&gt;</c>: the call takes the tree just built. Also
        /// <c>params Expression&lt;T&gt;[]</c>: Include-like helpers of the solution taking several trees.
        /// </summary>
        private static bool TakesTree(MethodSignature<TypeName> signature) =>
            signature.ParameterTypes.Any(p => p.Name is ExpressionType or ExpressionType + "[]");

        /// <summary>The method behind a token with its declaring type (a generic one by its definition); null for anything else.</summary>
        private MethodName? Method(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return new MethodName(_names.Of(method.GetDeclaringType()), reader.GetString(method.Name), IsGeneric: false);
                }
                case HandleKind.MemberReference:
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    return member.GetKind() == MemberReferenceKind.Method && _names.OfParent(member.Parent) is { } parent
                        ? new MethodName(parent, reader.GetString(member.Name), IsGeneric: false)
                        : null;
                }
                case HandleKind.MethodSpecification:
                    return Method(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method) is { } generic
                        ? generic with { IsGeneric = true }
                        : null;
                default:
                    return null;
            }
        }

        /// <summary>A getter, setter or <c>Set&lt;T&gt;()</c> of the solution; null for anything else.</summary>
        private (string Type, string Member, CodeUseKind Kind)? Use(MethodName method, int token, bool isToken)
        {
            if (!method.IsGeneric)
            {
                return Accessor(method.Declaring, method.Name, isToken);
            }

            if (isToken || method.Name != "Set" || method.Declaring.Name != DbContextType)
            {
                return null;
            }

            var arguments = reader.GetMethodSpecification((MethodSpecificationHandle)MetadataTokens.EntityHandle(token)).DecodeSignature(_names, null);
            return arguments is [{ IsDefined: true } entity] && solution.Contains(entity.Assembly!)
                ? (entity.Name, "", CodeUseKind.Query)
                : null;
        }

        private (string, string, CodeUseKind)? Accessor(TypeName type, string method, bool isToken)
        {
            if (!type.IsDefined || !solution.Contains(type.Assembly!))
            {
                return null;
            }

            if (method.StartsWith("get_", StringComparison.Ordinal))
            {
                return (type.Name, method[4..], isToken ? CodeUseKind.Expression : CodeUseKind.Read);
            }

            return method.StartsWith("set_", StringComparison.Ordinal) && !isToken
                ? (type.Name, method[4..], CodeUseKind.Write)
                : null;
        }

        private ImmutableArray<SequencePoint> SequencePoints(MethodDefinitionHandle method)
        {
            if (pdb is null)
            {
                return [];
            }

            try
            {
                return [.. pdb.GetMethodDebugInformation(method.ToDebugInformationHandle()).GetSequencePoints()];
            }
            catch (BadImageFormatException)
            {
                return [];
            }
        }

        /// <summary>The last visible sequence point at or before the instruction.</summary>
        private (string? Document, int? Line) Locate(ImmutableArray<SequencePoint> points, int offset)
        {
            SequencePoint? found = null;
            foreach (var point in points)
            {
                if (point.Offset > offset)
                {
                    break;
                }

                if (!point.IsHidden)
                {
                    found = point;
                }
            }

            return found is { } p ? (pdb!.GetString(pdb.GetDocument(p.Document).Name), p.StartLine) : (null, null);
        }
    }

    /// <summary>Where a method is written, as the developer sees it: lambdas, local functions and async/iterator state machines resolved.</summary>
    private sealed record Caller(string Type, string Method);

    /// <summary>A called or named method: <c>get_Email</c> of <c>Shop.Customer</c>; <see cref="IsGeneric"/> for a generic instantiation (<c>Set&lt;Customer&gt;</c>).</summary>
    private readonly record struct MethodName(TypeName Declaring, string Name, bool IsGeneric);

    private static class Callers
    {
        public static Caller Of(MetadataReader reader, TypeDefinitionHandle typeHandle, MethodDefinitionHandle methodHandle, TypeNames names)
        {
            var method = reader.GetString(reader.GetMethodDefinition(methodHandle).Name);
            var written = Written(method);
            var type = typeHandle;
            while (true)
            {
                var definition = reader.GetTypeDefinition(type);
                var name = reader.GetString(definition.Name);
                if (!name.StartsWith('<') || !definition.IsNested)
                {
                    break;
                }

                // <GetOffene>d__5 (async/iterator state machine) names the method; <>c and <>c__DisplayClass0_0 do not.
                written ??= Written(name);
                type = definition.GetDeclaringType();
            }

            return new Caller(names.Of(type).Name, written ?? method);
        }

        /// <summary><c>&lt;GetOffene&gt;b__0_0</c>, <c>&lt;GetOffene&gt;g__Local|0_0</c>, <c>&lt;GetOffene&gt;d__5</c> → GetOffene.</summary>
        private static string? Written(string name)
        {
            if (!name.StartsWith('<'))
            {
                return null;
            }

            var end = name.IndexOf('>', StringComparison.Ordinal);
            return end > 1 ? name[1..end] : null;
        }
    }
}

/// <summary>A type as a signature or token names it: full CLR name and the assembly defining it (null = unknown).</summary>
internal readonly record struct TypeName(string Name, string? Assembly)
{
    public bool IsDefined => Assembly is not null;
}

/// <summary>Full CLR names of types from definitions, references and signatures (generic instantiations by their definition).</summary>
internal sealed partial class TypeNames(MetadataReader reader, string assemblyName) : ISignatureTypeProvider<TypeName, object?>
{
    /// <summary>A <c>file class Helper</c> is emitted as <c>&lt;Helpers_cs&gt;F1A2B…__Helper</c>; shown as written.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^<[^>]*>F[0-9A-F]+__(.+)$")]
    private static partial System.Text.RegularExpressions.Regex FileLocal();

    public TypeName Of(TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = FileLocal().Match(reader.GetString(type.Name)) is { Success: true } local ? local.Groups[1].Value : reader.GetString(type.Name);
        if (type.IsNested)
        {
            return new TypeName(Of(type.GetDeclaringType()).Name + "+" + name, assemblyName);
        }

        var ns = reader.GetString(type.Namespace);
        return new TypeName(ns.Length == 0 ? name : ns + "." + name, assemblyName);
    }

    public TypeName Of(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => Of((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => Of((TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
        _ => new TypeName("?", null),
    };

    /// <summary>The declaring type of a member reference; null for a module or a vararg method.</summary>
    public TypeName? OfParent(EntityHandle parent) =>
        parent.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification ? Of(parent) : null;

    private TypeName Of(TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var scope = type.ResolutionScope;
        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
                var outer = Of((TypeReferenceHandle)scope);
                return new TypeName(outer.Name + "+" + name, outer.Assembly);
            case HandleKind.AssemblyReference:
                var ns = reader.GetString(type.Namespace);
                return new TypeName(ns.Length == 0 ? name : ns + "." + name,
                    reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
            default:
                // ModuleDefinition or ModuleReference: this assembly.
                var own = reader.GetString(type.Namespace);
                return new TypeName(own.Length == 0 ? name : own + "." + name, assemblyName);
        }
    }

    public TypeName GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle handle, byte rawTypeKind) => Of(handle);

    public TypeName GetTypeFromReference(MetadataReader _, TypeReferenceHandle handle, byte rawTypeKind) => Of(handle);

    public TypeName GetTypeFromSpecification(MetadataReader _, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, context);

    public TypeName GetGenericInstantiation(TypeName genericType, ImmutableArray<TypeName> typeArguments) => genericType;

    public TypeName GetPrimitiveType(PrimitiveTypeCode typeCode) => new("System." + typeCode, null);

    public TypeName GetArrayType(TypeName elementType, ArrayShape shape) => elementType with { Name = elementType.Name + "[]", Assembly = null };

    public TypeName GetSZArrayType(TypeName elementType) => elementType with { Name = elementType.Name + "[]", Assembly = null };

    public TypeName GetByReferenceType(TypeName elementType) => elementType with { Assembly = null };

    public TypeName GetPointerType(TypeName elementType) => elementType with { Assembly = null };

    public TypeName GetPinnedType(TypeName elementType) => elementType;

    public TypeName GetModifiedType(TypeName modifier, TypeName unmodifiedType, bool isRequired) => unmodifiedType;

    public TypeName GetFunctionPointerType(MethodSignature<TypeName> signature) => new("?", null);

    public TypeName GetGenericMethodParameter(object? genericContext, int index) => new("!!" + index, null);

    public TypeName GetGenericTypeParameter(object? genericContext, int index) => new("!" + index, null);
}

/// <summary>Opcode table for walking IL: which opcode a byte is and how long its operand is.</summary>
internal static class IlOpCodes
{
    public static readonly OpCode[] OneByte = new OpCode[256];

    public static readonly OpCode[] TwoByte = new OpCode[256];

    static IlOpCodes()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode code)
            {
                var value = (ushort)code.Value;
                if (code.Size == 1)
                {
                    OneByte[value] = code;
                }
                else
                {
                    TwoByte[value & 0xFF] = code;
                }
            }
        }
    }

    public static int OperandSize(OperandType type, ImmutableArray<byte> il, int offset) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il.AsSpan(offset, 4)),
        _ => 4,
    };
}
