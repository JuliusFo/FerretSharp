using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Tests.ClrModel.UsageSamples;

namespace FerretSharp.Core.Tests.ClrModel.Usage;

/// <summary>The index over this test assembly's samples with a model export as the model host would write it.</summary>
public sealed class CodeUsageIndexTests
{
    private const string Samples = "FerretSharp.Core.Tests.ClrModel.UsageSamples.";

    private static readonly Lazy<AssemblyUses> Scanned = new(() => IlUsageScanner.Scan(typeof(Customer).Assembly.Location, []));

    internal static PropertyExport Property(string name, string? column, bool shadow = false) =>
        new(name, "string", "System.String", true, shadow, column, null, null, null, false, null);

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
        Assert.False(index.IsUnused(order, order.Properties.Single(p => p.Name == "CustomerId")));
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
