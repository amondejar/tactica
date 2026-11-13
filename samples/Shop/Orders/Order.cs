using Shop.Kernel;

namespace Shop.Orders;

public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);

public sealed record OrderPlaced(OrderId Order, CustomerId Customer, DateTime OccurredOn) : IDomainEvent;
public sealed record OrderLineAdded(OrderId Order, string Product, int Quantity) : IDomainEvent;

/// <summary>An aggregate that guards its state: private setters, a read-only view of its lines, other aggregates by id.</summary>
public sealed class Order : AggregateRoot<OrderId>
{
    private readonly List<OrderLine> _lines = [];

    public CustomerId Customer { get; private set; }
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines;
    public Money Total => _lines.Aggregate(Money.Zero("EUR"), (sum, line) => sum.Add(line.Subtotal));

    private Order() { }

    private Order(OrderId id, CustomerId customer) : base(id)
    {
        Customer = customer;
        Status = OrderStatus.Draft;
    }

    public static Order Place(CustomerId customer)
    {
        var order = new Order(new OrderId(Guid.NewGuid()), customer);
        order.Raise(new OrderPlaced(order.Id, customer, DateTime.UtcNow));
        return order;
    }

    public void AddLine(string product, int quantity, Money unitPrice)
    {
        if (Status != OrderStatus.Draft) throw new InvalidOperationException("Only draft orders can change.");
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        _lines.Add(new OrderLine(_lines.Count + 1, product, quantity, unitPrice));
        Raise(new OrderLineAdded(Id, product, quantity));
    }

    public void Submit()
    {
        if (_lines.Count == 0) throw new InvalidOperationException("An order needs at least one line.");
        Status = OrderStatus.Submitted;
    }
}

public enum OrderStatus { Draft, Submitted }

public sealed class OrderLine : Entity<int>
{
    public string Product { get; private set; } = "";
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; } = Money.Zero("EUR");
    public Money Subtotal => UnitPrice.Times(Quantity);

    private OrderLine() { }

    internal OrderLine(int id, string product, int quantity, Money unitPrice) : base(id)
    {
        Product = product;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }
}

/// <summary>A value object: immutable, compared by all its values.</summary>
public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency) => (Amount, Currency) = (amount, currency);

    public static Money Of(decimal amount, string currency) =>
        amount < 0 ? throw new ArgumentOutOfRangeException(nameof(amount)) : new Money(amount, currency);

    public static Money Zero(string currency) => new(0, currency);

    public Money Add(Money other) =>
        other.Currency == Currency ? new Money(Amount + other.Amount, Currency) : throw new InvalidOperationException("Different currencies.");

    public Money Times(int quantity) => new(Amount * quantity, Currency);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }
}

public interface IOrderRepository
{
    Task<Order?> GetAsync(OrderId id, CancellationToken cancellationToken = default);
    void Add(Order order);
}
