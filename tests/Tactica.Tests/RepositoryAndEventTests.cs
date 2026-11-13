namespace Tactica.Tests;

public static class RepositoryTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Kernel;

        public readonly record struct OrderId(int Value);

        public sealed class OrderLine : Entity<int>
        {
            private OrderLine() { }
        }

        public sealed class Order : AggregateRoot<OrderId>
        {
            private readonly List<OrderLine> _lines = new();
            public IReadOnlyList<OrderLine> Lines => _lines;
            private Order() { }
            public void Cancel() { }
        }
        """;

    [Test]
    public static void Repositories_of_aggregate_roots_have_no_findings() => Verify.Diagnostics(Model + """

        public interface IOrderRepository
        {
            Task<Order?> GetAsync(OrderId id, CancellationToken cancellationToken = default);
            Task<IReadOnlyList<Order>> ListOpenAsync(CancellationToken cancellationToken = default);
            void Add(Order order);
            Task<int> CountLinesAsync(OrderId id);
        }

        public interface IRepository<T> where T : class { Task<T?> GetAsync(object id); }

        public sealed class OrderRepository : IOrderRepository
        {
            public Task<Order?> GetAsync(OrderId id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
            public Task<IReadOnlyList<Order>> ListOpenAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Order>>([]);
            public void Add(Order order) { }
            public Task<int> CountLinesAsync(OrderId id) => Task.FromResult(0);
            public IQueryable<OrderLine> Internals() => Enumerable.Empty<OrderLine>().AsQueryable();
        }
        """);

    [Test]
    public static void A_repository_for_an_inner_entity_is_reported()
    {
        var diagnostics = Verify.Diagnostics(Model + """

            public interface IRepository<T> where T : class { Task<T?> GetAsync(object id); }

            public interface {|TAC020:IOrderLineRepository|}
            {
                Task<OrderLine?> GetAsync(int id);
                void Remove(OrderLine line);
            }

            public interface {|TAC020:ILineRepositorio|} : IRepository<OrderLine> { }
            """);

        Assert.Contains("IOrderLineRepository loads or saves OrderLine, an entity inside an aggregate", diagnostics[0].GetMessage());
    }

    [Test]
    public static void Returning_IQueryable_is_reported() => Verify.Diagnostics(Model + """

        public interface IOrderRepository
        {
            IQueryable<Order> {|TAC021:Query|}();
            Task<IQueryable<Order>> {|TAC021:QueryAsync|}();
            IOrderedQueryable<Order> {|TAC021:Sorted|}();
            IEnumerable<Order> All();
        }
        """);

    [Test]
    public static void Suffixes_are_configurable() => Verify.Diagnostics(Model + """

        public interface IOrderLineStore
        {
            OrderLine? Find(int id);
        }

        public interface {|TAC020:ILineGateway|}
        {
            OrderLine? Find(int id);
        }
        """, new Dictionary<string, string> { ["tactica.repository_suffixes"] = "Gateway" });
}

public static class DomainEventTests
{
    [Test]
    public static void Immutable_events_have_no_findings() => Verify.Diagnostics("""
        using System;
        using Kernel;

        public sealed record OrderPlaced(int OrderId, DateTime OccurredOn) : IDomainEvent;

        public sealed class OrderCancelled : IDomainEvent
        {
            public int OrderId { get; }
            public string Reason { get; init; } = "";
            public OrderCancelled(int orderId) => OrderId = orderId;
        }
        """);

    [Test]
    public static void Mutable_events_are_reported() => Verify.Diagnostics("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class OrderShipped : IDomainEvent
        {
            public int OrderId { get; {|TAC030:set|}; }
            public string {|TAC030:Carrier|} = "";
            public List<int> {|TAC002:Parcels|} { get; init; } = new();
            public string Tracking { get; private set; } = "";
        }
        """);

    [Test]
    public static void Raising_an_event_from_outside_the_aggregate_is_reported()
    {
        var diagnostics = Verify.Diagnostics("""
            using System.Collections.Generic;
            using Kernel;

            public sealed record OrderPaid(int OrderId) : IDomainEvent;

            public abstract class Aggregate
            {
                private readonly List<IDomainEvent> _events = new();
                public void AddDomainEvent(IDomainEvent e) => _events.Add(e);
            }

            public sealed class Order : Aggregate
            {
                public int Id { get; private set; }
                private Order() { }
                public void Pay() => AddDomainEvent(new OrderPaid(Id));
                public void Notify(Order other) => other.AddDomainEvent(new OrderPaid(other.Id));
            }

            public sealed class PayOrderHandler
            {
                public void Handle(Order order)
                {
                    order.Pay();
                    {|TAC031:order.AddDomainEvent(new OrderPaid(order.Id))|};
                }
            }

            public sealed class Mailer
            {
                public void AddEvent(string text) { }
                public void Send(Mailer other) => other.AddEvent("x");
            }
            """);

        Assert.Contains("Handle raises OrderPaid on a Order from outside it", diagnostics.Single().GetMessage());
    }
}

public static class BaselineTests
{
    private const string Source = """
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; {|TAC001:set|}; } = "";
            public string Notes { get; set; } = "";
            private Order() { }
            public void Confirm() { }
        }
        """;

    [Test]
    public static void Baselined_findings_are_not_reported() => Verify.Diagnostics(Source, baseline: """
        # Tactica baseline
        TAC001 Order.Notes

        """);

    [Test]
    public static void Every_finding_carries_its_baseline_key()
    {
        var diagnostics = Verify.Run(Source.Replace("{|TAC001:set|}", "set"));
        var keys = diagnostics.Select(d => d.Properties["TacticaKey"]).ToList();
        Assert.Equal("TAC001 Order.Status|TAC001 Order.Notes", string.Join("|", keys));
    }

    [Test]
    public static void Baseline_mode_reports_everything() => Verify.Diagnostics(
        Source.Replace("public string Notes { get; set; }", "public string Notes { get; {|TAC001:set|}; }"),
        new Dictionary<string, string> { ["build_property.TacticaBaselineMode"] = "true" },
        baseline: "TAC001 Order.Notes");
}
