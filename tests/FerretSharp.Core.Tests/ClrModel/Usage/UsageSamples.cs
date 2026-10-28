// Code the IL scanner reads from this test assembly itself (WP-40): entities, a context, a configuration and methods that
// use them. Each use carries a marker comment the tests look up in the source to know its line. Do not reformat the lines
// with markers; the tests find them by the comment, not by number.
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

#pragma warning disable CA1822, CA1812, IDE0051 // read by the scanner, never run

namespace FerretSharp.Core.Tests.ClrModel.UsageSamples
{
    public abstract class EntityBase
    {
        public int Id { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public interface IHasName
    {
        string Name { get; }
    }

    public class Customer : EntityBase, IHasName
    {
        public string Name { get; set; } = "";

        public string? Email { get; set; }

        public string? Unused { get; set; }

        public List<Order> Orders { get; set; } = [];
    }

    public class Order : EntityBase
    {
        public int CustomerId { get; set; }

        public Customer? Customer { get; set; }

        public decimal Total { get; set; }

        public class Line
        {
            public int Quantity { get; set; }
        }
    }

    public class ShopContext : DbContext
    {
        public DbSet<Customer> Customers => Set<Customer>(); // marker: dbset-customers

        protected override void OnModelCreating(ModelBuilder builder) =>
            builder.Entity<Order>(o => o.Property(x => x.Total)); // marker: mapping-on-model-creating
    }

    public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
    {
        public void Configure(EntityTypeBuilder<Customer> builder) =>
            builder.Property(x => x.Email); // marker: mapping-configuration
    }

    public sealed class CustomerService(ShopContext context)
    {
        public string? ReadEmail(Customer customer) => customer.Email; // marker: read-email

        public void WriteEmail(Customer customer) => customer.Email = "a@b.c"; // marker: write-email

        public Customer Create() => new() { Name = "x" }; // marker: init-name

        public IQueryable<Customer> WithEmail(IQueryable<Customer> customers) =>
            customers.Where(c => c.Email != null); // marker: linq-email

        public async Task<int> CountOrdersAsync(Customer customer)
        {
            await Task.Yield();
            return customer.Orders.Count; // marker: async-orders
        }

        public IEnumerable<int> Ids(IEnumerable<EntityBase> entities) =>
            entities.Select(e => e.Id); // marker: lambda-base-id

        public string Named(IHasName named) => named.Name; // marker: interface-name

        public int Quantity(Order.Line line) => line.Quantity; // marker: nested-quantity

        public DbSet<Order> Orders() => context.Set<Order>(); // marker: set-orders

        public int Local(Order order)
        {
            return Inner();

            int Inner() => order.CustomerId; // marker: local-function
        }

        public int Framework(string text) => text.Length; // not kept: a framework type
    }

    /// <summary>Only for ExecuteUpdate: an entity of its own, so the index tests over Customer and Order stay as they are.</summary>
    public class Invoice
    {
        public string Number { get; set; } = "";

        public string? Note { get; set; }

        public decimal Amount { get; set; }
    }

    public sealed class InvoiceService
    {
        public int Rename(IQueryable<Invoice> invoices) =>
            invoices.ExecuteUpdate(s => s.SetProperty(i => i.Number, i => i.Number + i.Note).SetProperty(i => i.Note, (string?)null)); // marker: execute-update

        public int Raise(IQueryable<Invoice> invoices) =>
            invoices.ExecuteUpdate(s => s.SetProperty(i => i.Amount, i => i.Amount * 2)); // marker: execute-update-amount
    }

    /// <summary>Only for filters (Where, Any, First …): an entity of its own, so the index tests stay as they are.</summary>
    public class Ticket
    {
        public int Number { get; set; }

        public string Status { get; set; } = "";

        public string? Title { get; set; }

        public DateTime Due { get; set; }

        public string? Owner { get; set; }

        public List<TicketNote> Notes { get; set; } = [];
    }

    public class TicketNote
    {
        public string Text { get; set; } = "";

        public Ticket? Ticket { get; set; }
    }

    public static class TicketClock
    {
        public static Ticket Current { get; } = new();
    }

    public sealed class TicketRepository
    {
        public IQueryable<Ticket> Load(params Expression<Func<Ticket, object?>>[] includes) => Enumerable.Empty<Ticket>().AsQueryable();
    }

    public sealed class TicketService
    {
        private readonly Ticket _template = new();

        public IQueryable<Ticket> Follow(IQueryable<Ticket> tickets, Ticket parent, TicketNote note) =>
            tickets.Where(t => t.Number == parent.Number && t.Due == _template.Due && t.Title == note.Ticket!.Title); // marker: filter-values

        public IQueryable<Ticket> Sorted(IQueryable<Ticket> tickets, string sort)
        {
            Expression<Func<Ticket, object?>> key = sort switch
            {
                "title" => t => t.Title,
                _ => t => t.Due,
            };
            Expression<Func<Ticket, int>> number = t => t.Number;
            return tickets.Where(t => t.Status != "closed").OrderBy(key).ThenBy(number); // marker: filter-stored
        }

        public IQueryable<Ticket> Reversed(IQueryable<Ticket> tickets) =>
            tickets.Where(t => "open" == t.Status && null == t.Title && t.Due < TicketClock.Current.Due); // marker: filter-reversed

        public Ticket OrDefault(IQueryable<Ticket> tickets) => tickets.FirstOrDefault(t => t.Status == "open", new Ticket()); // marker: filter-default

        public IQueryable<Ticket> SortedByMap(IQueryable<Ticket> tickets, string sort)
        {
            var keys = new Dictionary<string, Expression<Func<Ticket, object?>>> { ["title"] = t => t.Title, ["due"] = t => t.Due };
            return tickets.Where(t => t.Status != "closed").OrderBy(keys[sort]); // marker: filter-map
        }

        public IQueryable<Ticket> Loaded(TicketRepository repository) =>
            repository.Load(t => t.Notes, t => t.Title).Where(t => t.Status == "open"); // marker: filter-params

        public bool AllClosed(IQueryable<Ticket> tickets) => tickets.All(t => t.Status == "closed");

        public IQueryable<string?> OpenTitles(IQueryable<Ticket> tickets) =>
            tickets.Where(t => t.Status == "open").OrderBy(t => t.Due).Select(t => t.Title); // marker: filter-where

        public Task<Ticket?> ByNumberAsync(IQueryable<Ticket> tickets, int number) =>
            tickets.FirstOrDefaultAsync(t => t.Number == number, CancellationToken.None); // marker: filter-async

        public bool WithNote(IQueryable<Ticket> tickets, string text) =>
            tickets.Any(t => t.Notes.Any(n => n.Text == text)); // marker: filter-nested

        public Expression<Func<Ticket, bool>> Mine(string owner) => t => t.Owner == owner; // marker: filter-returned

        public IQueryable<Ticket> Unowned(IQueryable<Ticket> tickets) =>
            Apply(tickets, t => t.Owner == null); // marker: filter-own-method

        private static IQueryable<Ticket> Apply(IQueryable<Ticket> tickets, Expression<Func<Ticket, bool>> condition) => tickets.Where(condition);
    }
}

// Stand-ins for EF Core's types, so the scanner sees the names it looks for without a package (net10.0 test project, EF 8
// is the user's). Only names and shapes matter.
namespace Microsoft.EntityFrameworkCore
{
    public class DbContext
    {
        public DbSet<T> Set<T>() where T : class => new();

        protected virtual void OnModelCreating(ModelBuilder builder)
        {
        }
    }

    public class DbSet<T> where T : class;

    public class ModelBuilder
    {
        public ModelBuilder Entity<T>(Action<EntityTypeBuilder<T>> configure) where T : class => this;
    }

    public class EntityTypeBuilder<T> where T : class
    {
        public EntityTypeBuilder<T> Property<TProperty>(Expression<Func<T, TProperty>> property) => this;
    }

    public interface IEntityTypeConfiguration<T> where T : class
    {
        void Configure(EntityTypeBuilder<T> builder);
    }

    public static class EntityFrameworkQueryableExtensions
    {
        public static Task<TSource?> FirstOrDefaultAsync<TSource>(
            this IQueryable<TSource> source,
            Expression<Func<TSource, bool>> predicate,
            CancellationToken cancellationToken = default) => Task.FromResult(default(TSource));
    }

    public static class RelationalQueryableExtensions
    {
        public static int ExecuteUpdate<TSource>(
            this IQueryable<TSource> source,
            Expression<Func<Query.SetPropertyCalls<TSource>, Query.SetPropertyCalls<TSource>>> setPropertyCalls) => 0;
    }
}

namespace Microsoft.EntityFrameworkCore.Query
{
    public sealed class SetPropertyCalls<TSource>
    {
        public SetPropertyCalls<TSource> SetProperty<TProperty>(Func<TSource, TProperty> propertyExpression, Func<TSource, TProperty> valueExpression) => this;

        public SetPropertyCalls<TSource> SetProperty<TProperty>(Func<TSource, TProperty> propertyExpression, TProperty valueExpression) => this;
    }
}
