using Shop.Kernel;
using Shop.Orders;

namespace Shop.Billing;

// The mistakes Tactica catches. Each comment names the rule.

public sealed class Invoice : AggregateRoot<Guid>
{
    public decimal Total { get; set; }                         // TAC001: anyone can change the total
    public List<string> Payments { get; } = [];                // TAC002: anyone can add a payment
    public Order Order { get; private set; } = null!;           // TAC003: holds another aggregate

    private Invoice() { }

    public void Issue() { }
}

public sealed class Address : ValueObject
{
    public string Street { get; set; } = "";                   // TAC010: a value that changes
    public string City { get; }                                // TAC012: left out of equality

    public Address(string street, string city) => (Street, City) = (street, city);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Street;
    }
}

public sealed class InvoicePaid : IDomainEvent
{
    public Guid InvoiceId { get; set; }                        // TAC030: an event that changes
}

public interface IOrderLineRepository                          // TAC020: a repository for an inner entity
{
    Task<OrderLine?> GetAsync(int id);
    IQueryable<Invoice> Query();                               // TAC021: leaks the query
}
