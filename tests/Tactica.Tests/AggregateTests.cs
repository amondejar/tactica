namespace Tactica.Tests;

public static class AggregateTests
{
    [Test]
    public static void A_well_formed_aggregate_has_no_findings() => Verify.Diagnostics("""
        using System;
        using System.Collections.Generic;
        using Kernel;

        public readonly record struct OrderId(Guid Value);
        public readonly record struct CustomerId(Guid Value);
        public sealed record OrderPlaced(OrderId Order) : IDomainEvent;

        public sealed class OrderLine : Entity<int>
        {
            public string Product { get; private set; } = "";
            public int Quantity { get; private set; }
            private OrderLine() { }
            internal OrderLine(int id, string product, int quantity) : base(id) { Product = product; Quantity = quantity; }
            internal void Increase(int by) => Quantity += by;
        }

        public sealed class Order : AggregateRoot<OrderId>
        {
            private readonly List<OrderLine> _lines = new();
            public CustomerId Customer { get; private set; }
            public IReadOnlyList<OrderLine> Lines => _lines;
            private Order() { }
            private Order(OrderId id, CustomerId customer) : base(id) => Customer = customer;
            public static Order Place(CustomerId customer)
            {
                var order = new Order(new OrderId(Guid.NewGuid()), customer);
                order.Raise(new OrderPlaced(order.Id));
                return order;
            }
            public void Add(string product, int quantity) => _lines.Add(new OrderLine(_lines.Count + 1, product, quantity));
        }
        """);

    [Test]
    public static void Public_and_internal_setters_are_reported() => Verify.Diagnostics("""
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; {|TAC001:set|}; } = "";
            public decimal Total { get; internal {|TAC001:set|}; }
            public string Notes { get; {|TAC001:init|}; } = "";
            public string Reference { get; private set; } = "";
            public int Version { get; protected set; }
            public string Computed => Status + Reference;
            private Order() { }
            public void Confirm() => Status = "Confirmed";
        }
        """);

    [Test]
    public static void Entities_inside_an_aggregate_follow_the_same_rules() => Verify.Diagnostics("""
        using Kernel;

        public sealed class OrderLine : Entity<int>
        {
            public int Quantity { get; {|TAC001:set|}; }
            public string {|TAC001:Note|} = "";
            private readonly string _product = "";
            private OrderLine() { }
        }
        """);

    [Test]
    public static void Setter_message_tells_what_to_write_instead()
    {
        var diagnostics = Verify.Diagnostics("""
            using Kernel;

            public sealed class Order : AggregateRoot<int>
            {
                public string Status { get; {|TAC001:set|}; } = "";
                private Order() { }
                public void Confirm() { }
            }
            """);

        var message = diagnostics.Single().GetMessage();
        Assert.Contains("Order.Status has a public set", message);
        Assert.Contains("Make it 'private set'", message);
        Assert.Equal("https://github.com/amondejar/tactica/blob/main/docs/rules.md#tac001", diagnostics.Single().Descriptor.HelpLinkUri);
    }

    [Test]
    public static void Exposed_mutable_collections_are_reported() => Verify.Diagnostics("""
        using System.Collections.Generic;
        using System.Collections.ObjectModel;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            private readonly List<string> _tags = new();
            public List<string> {|TAC002:Lines|} { get; } = new();
            public ICollection<string> {|TAC002:Notes|} { get; private set; } = new List<string>();
            public Dictionary<string, int> {|TAC002:Stock|} { get; } = new();
            public string[] {|TAC002:Codes|} { get; private set; } = [];
            public ObservableCollection<string> {|TAC002:Watchers|} { get; } = new();
            public byte[] RowVersion { get; private set; } = [];
            public IReadOnlyList<string> Tags => _tags;
            public IEnumerable<string> Labels => _tags;
            private List<string> Hidden { get; } = new();
            private Order() { }
            public void Tag(string tag) => _tags.Add(tag);
        }
        """);

    [Test]
    public static void Collection_message_suggests_a_read_only_view_and_a_method()
    {
        var message = Verify.Diagnostics("""
            using System.Collections.Generic;
            using Kernel;

            public sealed class Order : AggregateRoot<int>
            {
                public List<decimal> {|TAC002:Payments|} { get; } = new();
                private Order() { }
                public void Pay(decimal amount) => Payments.Add(amount);
            }
            """).Single().GetMessage();

        Assert.Contains("IReadOnlyList<decimal>", message);
        Assert.Contains("AddPayment(...)", message);
    }

    [Test]
    public static void Holding_another_aggregate_is_reported_with_its_id_type()
    {
        var diagnostics = Verify.Diagnostics("""
            using System.Collections.Generic;
            using Kernel;

            public readonly record struct CustomerId(int Value);

            public sealed class Customer : AggregateRoot<CustomerId>
            {
                public string Name { get; private set; } = "";
                private Customer() { }
                public void Rename(string name) => Name = name;
            }

            public sealed class Order : AggregateRoot<int>
            {
                public Customer {|TAC003:Customer|} { get; private set; } = null!;
                private readonly List<Customer> {|TAC003:_reviewers|} = new();
                public Customer? {|TAC003:Referrer|} { get; private set; }
                public CustomerId Owner { get; private set; }
                private Order() { }
                public void Assign(Customer customer) => Customer = customer;
            }
            """);

        Assert.Contains("Reference it by id instead (CustomerId)", diagnostics[0].GetMessage());
        Assert.Contains("(a collection of CustomerId)", diagnostics[1].GetMessage());
    }

    [Test]
    public static void An_entity_holding_an_aggregate_is_reported() => Verify.Diagnostics("""
        using Kernel;

        public sealed class Product : AggregateRoot<int>
        {
            private Product() { }
            public void Discontinue() { }
        }

        public sealed class OrderLine : Entity<int>
        {
            public Product {|TAC003:Product|} { get; private set; } = null!;
            private OrderLine() { }
        }
        """);

    [Test]
    public static void Public_parameterless_constructors_are_reported() => Verify.Diagnostics("""
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; private set; } = "";
            public {|TAC005:Order|}() { }
            public void Confirm() => Status = "Confirmed";
        }

        public sealed class {|TAC005:Invoice|} : AggregateRoot<int>
        {
            public decimal Total { get; private set; }
            public void Pay() { }
        }

        public sealed class Shipment : AggregateRoot<int>
        {
            public string Carrier { get; private set; } = "";
            public Shipment(string carrier) => Carrier = carrier;
            protected Shipment() { }
            public void Dispatch() { }
        }

        public abstract class Document : AggregateRoot<int>
        {
            public string Title { get; private set; } = "";
            public void Rename(string title) => Title = title;
        }
        """);

    [Test]
    public static void An_aggregate_with_setters_and_no_methods_is_anemic() => Verify.Diagnostics("""
        using Kernel;

        public sealed class {|TAC006:Order|} : AggregateRoot<int>
        {
            public string Status { get; {|TAC001:set|}; } = "";
            public decimal Total { get; {|TAC001:set|}; }
            private Order() { }
            public override string ToString() => Status;
        }

        public sealed class Rate : AggregateRoot<int>
        {
            public decimal Value { get; private set; }
            private Rate() { }
            public static Rate Create(decimal value) => new() { Value = value };
        }
        """);

    [Test]
    public static void Markers_can_be_attributes_or_interfaces_from_other_libraries() => Verify.Diagnostics("""
        using System;

        public interface IAggregateRoot { }
        public abstract class EntityBase { public Guid Id { get; protected set; } }

        [AttributeUsage(AttributeTargets.Class)]
        public sealed class AggregateRootAttribute : Attribute { }

        public sealed class Cart : EntityBase, IAggregateRoot
        {
            public decimal Total { get; {|TAC001:set|}; }
            private Cart() { }
            public void Clear() => Total = 0;
        }

        [AggregateRoot]
        public sealed class Basket
        {
            public int Items { get; {|TAC001:set|}; }
            private Basket() { }
            public void Empty() => Items = 0;
        }
        """, kernel: false);

    [Test]
    public static void Configured_base_types_are_recognized() => Verify.Diagnostics("""
        namespace Acme.Domain
        {
            public abstract class Root { }
            public abstract class Part { }

            public sealed class Order : Root
            {
                public int Count { get; {|TAC001:set|}; }
                private Order() { }
                public void Reset() => Count = 0;
            }

            public sealed class Line : Part
            {
                public int Quantity { get; {|TAC001:set|}; }
                private Line() { }
            }
        }
        """, new Dictionary<string, string>
        {
            ["build_property.TacticaAggregateRoots"] = "Acme.Domain.Root",
            ["tactica.entities"] = "Part",
        }, kernel: false);

    [Test]
    public static void Default_conventions_can_be_turned_off() => Verify.Diagnostics("""
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; set; } = "";
        }
        """, new Dictionary<string, string> { ["build_property.TacticaDefaultConventions"] = "false" });

    [Test]
    public static void The_example_file_is_named_in_the_message()
    {
        var message = Verify.Diagnostics("""
            using Kernel;

            public sealed class Order : AggregateRoot<int>
            {
                public string Status { get; {|TAC001:set|}; } = "";
                private Order() { }
                public void Confirm() { }
            }
            """, new Dictionary<string, string> { ["build_property.TacticaExampleAggregate"] = "src/Domain/Shipping/Shipment.cs" })
            .Single().GetMessage();

        Assert.Contains("Follow the pattern in src/Domain/Shipping/Shipment.cs.", message);
    }

    [Test]
    public static void Types_that_are_not_part_of_the_model_are_ignored() => Verify.Diagnostics("""
        using System.Collections.Generic;

        public sealed class OrderDto
        {
            public string Status { get; set; } = "";
            public List<string> Lines { get; set; } = new();
        }

        public sealed class OrderPage
        {
            public int Count;
        }
        """);
}
