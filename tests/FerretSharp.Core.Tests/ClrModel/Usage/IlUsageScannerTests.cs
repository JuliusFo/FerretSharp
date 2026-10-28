using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Tests.ClrModel.UsageSamples;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>The IL scanner against this test assembly (<c>UsageSamples.cs</c>): real compiler output with a portable PDB.</summary>
public sealed class IlUsageScannerTests
{
    private const string Samples = "FerretSharp.Core.Tests.ClrModel.UsageSamples.";

    private static readonly Lazy<AssemblyUses> Scanned = new(() => IlUsageScanner.Scan(typeof(Customer).Assembly.Location, []));

    private static AssemblyUses Uses => Scanned.Value;

    /// <summary>The line of the marker comment in the samples' source (the PDB names the file as it was built).</summary>
    private static int LineOf(string marker)
    {
        var document = Uses.Uses.First(u => u.Document?.EndsWith("UsageSamples.cs", StringComparison.OrdinalIgnoreCase) == true).Document!;
        var lines = File.ReadAllLines(document);
        return Array.FindIndex(lines, l => l.Contains("// marker: " + marker, StringComparison.Ordinal)) + 1;
    }

    private static CodeUse Single(string type, string member, CodeUseKind kind, string callerMethod) =>
        Assert.Single(Uses.Uses, u => u.Type == Samples + type && u.Member == member && u.Kind == kind && u.CallerMethod == callerMethod);

    [Fact]
    public void A_getter_call_is_a_read_with_file_and_line()
    {
        var use = Single("Customer", "Email", CodeUseKind.Read, "ReadEmail");

        Assert.Equal(Samples + "CustomerService", use.CallerType);
        Assert.EndsWith("UsageSamples.cs", use.Document);
        Assert.Equal(LineOf("read-email"), use.Line);
        Assert.True(Uses.HasPdb);
        Assert.True(Uses.IsTest);
    }

    [Fact]
    public void Setters_and_object_initializers_are_writes()
    {
        Assert.Equal(LineOf("write-email"), Single("Customer", "Email", CodeUseKind.Write, "WriteEmail").Line);
        Assert.Equal(LineOf("init-name"), Single("Customer", "Name", CodeUseKind.Write, "Create").Line);
    }

    [Fact]
    public void An_expression_tree_names_the_getter_and_counts_for_the_method_it_is_written_in()
    {
        var use = Single("Customer", "Email", CodeUseKind.Filter, "WithEmail");

        Assert.Equal(Samples + "CustomerService", use.CallerType);
        Assert.Equal(LineOf("linq-email"), use.Line);
    }

    [Fact]
    public void The_property_an_execute_update_sets_is_a_write_and_its_value_a_read()
    {
        var line = LineOf("execute-update");

        Assert.Equal(line, Single("Invoice", "Number", CodeUseKind.Write, "Rename").Line);
        Assert.Equal(line, Single("Invoice", "Note", CodeUseKind.Write, "Rename").Line);
        // The value i => i.Number + i.Note reads both.
        Assert.Equal(line, Single("Invoice", "Number", CodeUseKind.Expression, "Rename").Line);
        Assert.Equal(line, Single("Invoice", "Note", CodeUseKind.Expression, "Rename").Line);
        Assert.Equal(LineOf("execute-update-amount"), Single("Invoice", "Amount", CodeUseKind.Write, "Raise").Line);
        Assert.Equal(LineOf("execute-update-amount"), Single("Invoice", "Amount", CodeUseKind.Expression, "Raise").Line);
    }

    [Fact]
    public void The_condition_of_where_any_and_first_is_a_filter_other_lambdas_are_not()
    {
        var line = LineOf("filter-where");

        Assert.Equal(line, Single("Ticket", "Status", CodeUseKind.Filter, "OpenTitles").Line);
        Assert.Equal(line, Single("Ticket", "Due", CodeUseKind.Expression, "OpenTitles").Line);
        Assert.Equal(line, Single("Ticket", "Title", CodeUseKind.Expression, "OpenTitles").Line);
        // EF's async operator, with a call for the token between the tree and the operator.
        Assert.Equal(LineOf("filter-async"), Single("Ticket", "Number", CodeUseKind.Filter, "ByNumberAsync").Line);
        // A nested lambda belongs to the condition of the outer operator.
        Single("Ticket", "Notes", CodeUseKind.Filter, "WithNote");
        Single("TicketNote", "Text", CodeUseKind.Filter, "WithNote");
    }

    [Fact]
    public void A_tree_returned_or_passed_to_own_code_is_no_filter_its_use_is_not_known()
    {
        Assert.Equal(LineOf("filter-returned"), Single("Ticket", "Owner", CodeUseKind.Expression, "Mine").Line);
        Assert.Equal(LineOf("filter-own-method"), Single("Ticket", "Owner", CodeUseKind.Expression, "Unowned").Line);
        Assert.DoesNotContain(Uses.Uses, u => u.Type == Samples + "Ticket" && u.Kind == CodeUseKind.Filter && u.Member == "Owner");
    }

    [Fact]
    public void A_value_the_condition_compares_with_is_no_filter()
    {
        // t.Number == parent.Number: the row's Number is filtered by, the parent's only gives the value (also from a
        // field of this and along a chain of properties).
        var line = LineOf("filter-values");
        foreach (var member in new[] { "Number", "Due", "Title" })
        {
            Assert.Equal(line, Single("Ticket", member, CodeUseKind.Filter, "Follow").Line);
            Assert.Equal(line, Single("Ticket", member, CodeUseKind.Expression, "Follow").Line);
        }

        Single("TicketNote", "Ticket", CodeUseKind.Expression, "Follow");
        Assert.DoesNotContain(Uses.Uses, u => u is { Member: "Ticket", Kind: CodeUseKind.Filter });
    }

    [Fact]
    public void A_getter_on_the_parameter_after_a_constant_is_a_filter()
    {
        // "open" == t.Status: the constant comes first, the parameter's getter after it is still filtered by. A static
        // property (TicketClock.Current.Due) is a value.
        var line = LineOf("filter-reversed");
        Assert.Equal(line, Single("Ticket", "Status", CodeUseKind.Filter, "Reversed").Line);
        Assert.Equal(line, Single("Ticket", "Title", CodeUseKind.Filter, "Reversed").Line);
        Assert.Equal(line, Single("Ticket", "Due", CodeUseKind.Filter, "Reversed").Line);
        Assert.Equal(line, Single("Ticket", "Due", CodeUseKind.Expression, "Reversed").Line);
        Single("TicketClock", "Current", CodeUseKind.Expression, "Reversed");
    }

    [Fact]
    public void A_tree_put_into_a_collection_is_no_filter()
    {
        Assert.Equal(LineOf("filter-map"), Single("Ticket", "Status", CodeUseKind.Filter, "SortedByMap").Line);
        Single("Ticket", "Title", CodeUseKind.Expression, "SortedByMap");
        Single("Ticket", "Due", CodeUseKind.Expression, "SortedByMap");
        Assert.DoesNotContain(Uses.Uses, u => u is { Kind: CodeUseKind.Filter, CallerMethod: "SortedByMap" } && u.Member != "Status");
    }

    [Fact]
    public void A_tree_kept_in_a_variable_or_passed_in_a_params_array_is_no_filter()
    {
        Assert.Equal(LineOf("filter-stored"), Single("Ticket", "Status", CodeUseKind.Filter, "Sorted").Line);
        Single("Ticket", "Title", CodeUseKind.Expression, "Sorted");
        Single("Ticket", "Due", CodeUseKind.Expression, "Sorted");
        Single("Ticket", "Number", CodeUseKind.Expression, "Sorted");

        Assert.Equal(LineOf("filter-params"), Single("Ticket", "Status", CodeUseKind.Filter, "Loaded").Line);
        Single("Ticket", "Notes", CodeUseKind.Expression, "Loaded");
        Single("Ticket", "Title", CodeUseKind.Expression, "Loaded");
        Assert.DoesNotContain(Uses.Uses, u => u is { Kind: CodeUseKind.Filter, CallerMethod: "Sorted" or "Loaded" } && u.Member != "Status");
    }

    [Fact]
    public void The_condition_of_all_is_no_filter()
    {
        // NOT EXISTS (… WHERE NOT …): an index on the column seldom helps there.
        Single("Ticket", "Status", CodeUseKind.Expression, "AllClosed");
    }

    [Fact]
    public void Lambdas_async_methods_and_local_functions_resolve_to_the_method_as_written()
    {
        Assert.Equal(LineOf("async-orders"), Single("Customer", "Orders", CodeUseKind.Read, "CountOrdersAsync").Line);
        Assert.Equal(LineOf("lambda-base-id"), Single("EntityBase", "Id", CodeUseKind.Read, "Ids").Line);
        Assert.Equal(LineOf("local-function"), Single("Order", "CustomerId", CodeUseKind.Read, "Local").Line);
        Assert.All(Uses.Uses.Where(u => u.CallerType.StartsWith(Samples, StringComparison.Ordinal)), u => Assert.DoesNotContain('<', u.CallerType));
    }

    [Fact]
    public void A_base_class_interface_or_nested_type_is_named_as_the_il_names_it()
    {
        Single("EntityBase", "Id", CodeUseKind.Read, "Ids");
        Single("IHasName", "Name", CodeUseKind.Read, "Named");
        Assert.Equal(LineOf("nested-quantity"), Single("Order+Line", "Quantity", CodeUseKind.Read, "Quantity").Line);
    }

    [Fact]
    public void Set_of_t_on_a_context_is_a_query_of_the_entity()
    {
        Assert.Equal(LineOf("set-orders"), Single("Order", "", CodeUseKind.Query, "Orders").Line);
        Assert.Equal(LineOf("dbset-customers"), Single("Customer", "", CodeUseKind.Query, "get_Customers").Line);
    }

    [Fact]
    public void Mapping_code_is_recorded_with_its_caller_so_the_index_can_tell_it_apart()
    {
        var configuration = Single("Customer", "Email", CodeUseKind.Expression, "Configure");
        var onModelCreating = Single("Order", "Total", CodeUseKind.Expression, "OnModelCreating");

        Assert.Equal(Samples + "CustomerConfiguration", configuration.CallerType);
        Assert.Equal(LineOf("mapping-configuration"), configuration.Line);
        Assert.Equal(Samples + "ShopContext", onModelCreating.CallerType);
    }

    [Fact]
    public void Framework_members_and_unused_properties_are_not_kept()
    {
        Assert.DoesNotContain(Uses.Uses, u => u.Type == "System.String");
        Assert.DoesNotContain(Uses.Uses, u => u.CallerMethod == "Framework");
        Assert.DoesNotContain(Uses.Uses, u => u.Type == Samples + "Customer" && u.Member == "Unused");
    }

    [Fact]
    public void A_file_a_build_deleted_meanwhile_is_an_io_error_not_a_null_reference()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"ferret-gone-{Guid.NewGuid():N}.dll");

        Assert.Throws<FileNotFoundException>(() => IlUsageScanner.Scan(gone, []));
        Assert.Throws<FileNotFoundException>(() => IlUsageScanner.ReadMvid(gone));
    }

    [Fact]
    public void Types_carry_base_type_and_interfaces_for_inheritance()
    {
        var customer = Assert.Single(Uses.Types, t => t.Type == Samples + "Customer");

        Assert.Equal(Samples + "EntityBase", customer.BaseType);
        Assert.Equal([Samples + "IHasName"], customer.Interfaces);
        Assert.Equal(["Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1"],
            Assert.Single(Uses.Types, t => t.Type == Samples + "CustomerConfiguration").Interfaces);
        Assert.DoesNotContain(Uses.Types, t => t.Type.Contains('<'));
    }

    [Fact]
    public void Only_types_of_the_solution_count_and_the_mvid_identifies_the_build()
    {
        var path = typeof(Customer).Assembly.Location;

        Assert.Equal(typeof(Customer).Assembly.ManifestModule.ModuleVersionId, Uses.Mvid);
        Assert.Equal(Uses.Mvid, IlUsageScanner.ReadMvid(path));
        Assert.Equal(["FerretSharp.Core.Tests"], Uses.SolutionAssemblies);
        // Core's own types are another assembly: not kept unless named as part of the solution.
        Assert.DoesNotContain(Uses.Uses, u => u.Type.StartsWith("FerretSharp.Core.ClrModel.", StringComparison.Ordinal));
        Assert.Contains(IlUsageScanner.Scan(path, ["FerretSharp.Core"]).Uses, u => u.Type.StartsWith("FerretSharp.Core.", StringComparison.Ordinal) && !u.Type.StartsWith("FerretSharp.Core.Tests", StringComparison.Ordinal));
    }
}
