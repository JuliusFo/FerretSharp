using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Tests.ClrModel.UsageSamples;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>The index over this test assembly's samples with a model export as the model host would write it.</summary>
public sealed class CodeUsageIndexTests
{
    private const string Samples = "FerretSharp.Core.Tests.ClrModel.UsageSamples.";

    private static readonly Lazy<AssemblyUses> Scanned = new(() => IlUsageScanner.Scan(typeof(Customer).Assembly.Location, []));

    internal static PropertyExport Property(string name, string? column, bool shadow = false) =>
        new(name, "string", true, shadow, column, null, null, null, false, null);

    internal static ModelExport Model()
    {
        EntityExport Entity(string name, string table, IReadOnlyList<PropertyExport> properties, IReadOnlyList<ForeignKeyExport>? keys = null, string? dbSet = null) =>
            new(name, Samples + name, false, null, table, "SHOP", null, null, properties, ["Id"], keys ?? [], dbSet);

        return new ModelExport(ModelExport.CurrentFormatVersion, "8.0.0", Samples + "ShopContext", "options", null,
        [
            Entity("Customer", "CUSTOMERS",
            [
                Property("Id", "ID"), Property("CreatedAt", "CREATED_AT"), Property("Name", "NAME"), Property("Email", "EMAIL"),
                Property("Unused", "UNUSED"), Property("Version", "VERSION", shadow: true),
            ], dbSet: "Customers"),
            Entity("Order", "ORDERS",
            [
                Property("Id", "ID"), Property("CreatedAt", "CREATED_AT"), Property("CustomerId", "CUSTOMER_ID"), Property("Total", "TOTAL"),
            ], [new ForeignKeyExport(["CustomerId"], "Customer", ["Id"], "Customer", "Orders", false)]),
        ]);
    }

    private static CodeUsageIndex Index(bool asTest = false)
    {
        var uses = Scanned.Value;
        return CodeUsageIndex.Build(Model(), [uses with { IsTest = asTest }]);
    }

    private static EntityExport Entity(CodeUsageIndex index, string name) => index.Model.Entities.Single(e => e.Name == name);

    [Fact]
    public void A_property_has_its_reads_writes_and_expressions_application_code_first()
    {
        var index = Index();

        var uses = index.ForProperty(Entity(index, "Customer"), "Email");

        // The configuration comes first in the file, but mapping code goes after the application's uses.
        Assert.Equal(["ReadEmail", "WriteEmail", "WithEmail", "Configure"], uses.Select(u => u.Use.CallerMethod).ToList());
        Assert.True(uses[^1].IsMapping);
        Assert.All(uses.Take(3), u => Assert.False(u.IsMapping));
    }

    [Fact]
    public void Uses_through_a_base_class_or_interface_count_for_every_entity_with_the_property()
    {
        var index = Index();

        var id = Assert.Single(index.ForProperty(Entity(index, "Order"), "Id"));
        var name = Assert.Single(index.ForProperty(Entity(index, "Customer"), "Name"), u => u.Use.CallerMethod == "Named");

        Assert.Equal(Samples + "EntityBase", id.Via);
        Assert.Equal(Samples + "IHasName", name.Via);
        Assert.Equal(Samples + "EntityBase", Assert.Single(index.ForProperty(Entity(index, "Customer"), "Id")).Via);
    }

    [Fact]
    public void Configuration_on_model_creating_and_dbset_properties_are_mapping()
    {
        var index = Index();

        Assert.True(Assert.Single(index.ForProperty(Entity(index, "Order"), "Total")).IsMapping);
        var queries = index.ForEntity(Entity(index, "Customer"));
        Assert.True(Assert.Single(queries).IsMapping); // Set<Customer>() inside the DbSet property
        Assert.False(Assert.Single(index.ForEntity(Entity(index, "Order"))).IsMapping);
    }

    [Fact]
    public void Unused_leaves_out_mapping_shadow_properties_and_counts_a_foreign_key_through_its_navigation()
    {
        var index = Index();
        var customer = Entity(index, "Customer");
        var order = Entity(index, "Order");

        // Id is the primary key of both (and Customer.Id the principal key of Order.CustomerId): never unused.
        Assert.Equal(["Customer.CreatedAt", "Customer.Unused", "Order.CreatedAt", "Order.Total"],
            index.Unused().Select(u => u.Entity.Name + "." + u.Property.Name).Order().ToList());
        Assert.False(index.IsUnused(customer, customer.Properties.Single(p => p.Name == "Version")));
        // CustomerId is read in Local(); without that, Customer.Orders (the inverse navigation, read in CountOrdersAsync) counts.
        Assert.False(index.IsUnused(order, order.Properties.Single(p => p.Name == "CustomerId")));
    }

    [Fact]
    public void A_foreign_key_read_only_through_its_inverse_navigation_is_used()
    {
        var scanned = Scanned.Value;
        var withoutKey = scanned with { IsTest = false, Uses = scanned.Uses.Where(u => u.Member != "CustomerId").ToList() };
        var index = CodeUsageIndex.Build(Model(), [withoutKey]);
        var order = Entity(index, "Order");

        Assert.Empty(index.ForProperty(order, "CustomerId"));
        Assert.Equal("Customer.Orders", Assert.Single(index.ForColumn(order, "CustomerId")).Navigation);
        Assert.False(index.IsUnused(order, order.Properties.Single(p => p.Name == "CustomerId")));
    }

    [Fact]
    public void A_foreign_key_column_lists_its_own_uses_and_those_of_its_navigations()
    {
        var index = Index();
        var order = Entity(index, "Order");

        var uses = index.ForColumn(order, "CustomerId");

        // Local() reads CustomerId itself, CountOrdersAsync the inverse navigation Customer.Orders.
        Assert.Equal([("Local", null), ("CountOrdersAsync", "Customer.Orders")],
            uses.Select(u => (u.Use.CallerMethod, u.Navigation)).OrderBy(u => u.Navigation).ToList());
        Assert.All(index.ForColumn(order, "Total"), u => Assert.Null(u.Navigation));
    }

    [Fact]
    public void A_shadow_foreign_key_is_used_through_its_navigation_alone()
    {
        var navigation = new CodeUse(Samples + "Order", "Customer", CodeUseKind.Read, "Shop.Billing", "Print", "Billing.cs", 7);
        var model = Model();
        var shadow = model with
        {
            Entities = model.Entities.Select(e => e.Name != "Order" ? e : e with
            {
                Properties = e.Properties.Select(p => p.Name == "CustomerId" ? p with { IsShadow = true } : p).ToList(),
            }).ToList(),
        };
        var index = CodeUsageIndex.Build(shadow, [Scanned.Value with { IsTest = false, Uses = [navigation] }]);

        var use = Assert.Single(index.ForColumn(Entity(index, "Order"), "CustomerId"));

        Assert.Equal(("Print", "Order.Customer"), (use.Use.CallerMethod, use.Navigation));
    }

    [Fact]
    public void Navigations_to_an_entity_load_its_rows_from_either_side()
    {
        var navigation = new CodeUse(Samples + "Order", "Customer", CodeUseKind.Expression, "Shop.Billing", "WithCustomer", "Billing.cs", 9);
        var scanned = Scanned.Value;
        var index = CodeUsageIndex.Build(Model(), [scanned with { IsTest = false, Uses = [.. scanned.Uses, navigation] }]);

        // Include(o => o.Customer) loads customers, customer.Orders loads orders; neither names the other's set.
        Assert.Equal(["Order.Customer"], index.NavigationsTo(Entity(index, "Customer")).Select(u => u.Navigation));
        Assert.Equal(["CountOrdersAsync"], index.NavigationsTo(Entity(index, "Order")).Select(u => u.Use.CallerMethod));
        Assert.All(index.ForEntity(Entity(index, "Customer")), u => Assert.Null(u.Navigation));
    }

    [Fact]
    public void Keys_are_never_unused()
    {
        var scanned = Scanned.Value;
        var index = CodeUsageIndex.Build(Model(), [scanned with { IsTest = false, Uses = [] }]);
        var customer = Entity(index, "Customer");

        Assert.False(index.IsUnused(customer, customer.Properties.Single(p => p.Name == "Id")));
        Assert.True(index.IsUnused(customer, customer.Properties.Single(p => p.Name == "Email")));
        Assert.DoesNotContain(index.Unused(), u => u.Property.Name == "Id");
    }

    [Fact]
    public void Uses_in_a_test_assembly_are_shown_but_do_not_count()
    {
        var index = Index(asTest: true);
        var customer = Entity(index, "Customer");

        Assert.All(index.ForProperty(customer, "Email"), u => Assert.True(u.IsTest));
        Assert.True(index.IsUnused(customer, customer.Properties.Single(p => p.Name == "Email")));
    }
}
