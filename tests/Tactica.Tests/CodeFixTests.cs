namespace Tactica.Tests;

public static class CodeFixTests
{
    [Test]
    public static void TAC001_makes_setters_and_fields_private() => Verify.Fix("""
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; set; } = "";
            public decimal Total { get; internal set; }
            public string Notes { get; init; } = "";
            public int Count;
            private Order() { }
            public void Confirm() => Status = "Confirmed";
        }
        """, """
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; private set; } = "";
            public decimal Total { get; private set; }
            public string Notes { get; private set; } = "";
            private int Count;
            private Order() { }
            public void Confirm() => Status = "Confirmed";
        }
        """);

    [Test]
    public static void TAC002_exposes_a_read_only_view_over_a_private_field() => Verify.Fix("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public List<string> Lines { get; } = new();
            public Dictionary<string, int> Stock { get; private set; }
            private Order() { Stock = new Dictionary<string, int>(); }
            public void Add(string line)
            {
                Lines.Add(line);
                this.Stock[line] = Lines.Count;
            }
        }
        """, """
        using System.Collections.Generic;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            private readonly List<string> _lines = new();
            public IReadOnlyList<string> Lines => _lines;
            private readonly Dictionary<string, int> _stock;
            public IReadOnlyDictionary<string, int> Stock => _stock;
            private Order() { _stock = new Dictionary<string, int>(); }
            public void Add(string line)
            {
                _lines.Add(line);
                this._stock[line] = _lines.Count;
            }
        }
        """);

    [Test]
    public static void TAC002_keeps_the_field_writable_when_a_method_replaces_the_collection() => Verify.Fix("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string[] Codes { get; private set; } = [];
            private Order() { }
            public void Recode(string[] codes) => Codes = codes;
        }
        """, """
        using System.Collections.Generic;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            private string[] _codes = [];
            public IReadOnlyList<string> Codes => _codes;
            private Order() { }
            public void Recode(string[] codes) => _codes = codes;
        }
        """);

    [Test]
    public static void TAC005_makes_the_constructor_private_or_adds_one() => Verify.Fix("""
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; private set; } = "";
            public Order() { }
            public void Confirm() => Status = "Confirmed";
        }

        public sealed class Invoice : AggregateRoot<int>
        {
            public decimal Total { get; private set; }

            public void Pay() { }
        }
        """, """
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; private set; } = "";
            private Order() { }
            public void Confirm() => Status = "Confirmed";
        }

        public sealed class Invoice : AggregateRoot<int>
        {
            public decimal Total { get; private set; }

            private Invoice()
            {
            }

            public void Pay() { }
        }
        """);

    [Test]
    public static void TAC010_and_TAC012_make_a_value_object_immutable_and_complete() => Verify.Fix("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class Money : ValueObject
        {
            public decimal Amount { get; set; }
            public string Currency { get; }
            public int Scale;
            public Money(decimal amount, string currency, int scale) { Amount = amount; Currency = currency; Scale = scale; }
            protected override IEnumerable<object?> GetEqualityComponents()
            {
                yield return Amount;
            }
        }
        """, """
        using System.Collections.Generic;
        using Kernel;

        public sealed class Money : ValueObject
        {
            public decimal Amount { get; }
            public string Currency { get; }
            public readonly int Scale;
            public Money(decimal amount, string currency, int scale) { Amount = amount; Currency = currency; Scale = scale; }
            protected override IEnumerable<object?> GetEqualityComponents()
            {
                yield return Amount;
                yield return Currency;
                yield return Scale;
            }
        }
        """);

    [Test]
    public static void TAC013_adds_value_equality() => Verify.Fix("""
        public interface IValueObject { }

        public sealed class Email : IValueObject
        {
            public string Value { get; }
            public string Domain { get; }
            public Email(string value, string domain) => (Value, Domain) = (value, domain);
        }
        """, """
        public interface IValueObject { }

        public sealed class Email : IValueObject
        {
            public string Value { get; }
            public string Domain { get; }
            public Email(string value, string domain) => (Value, Domain) = (value, domain);
            public override bool Equals(object? obj) => obj is Email other && Equals(Value, other.Value) && Equals(Domain, other.Domain);
            public override int GetHashCode() => System.HashCode.Combine(Value, Domain);
        }
        """, kernel: false);

    [Test]
    public static void TAC013_equality_works_at_runtime()
    {
        // The generated members must give value semantics, not only compile.
        var fixedSource = Verify.Fixed("""
            public interface IValueObject { }

            public sealed class Email : IValueObject
            {
                public string Value { get; }
                public Email(string value) => Value = value;
            }
            """, kernel: false);

        var email = Verify.Emit(fixedSource).GetType("Email")!;
        object Create(string value) => Activator.CreateInstance(email, value)!;
        Assert.True(Create("a@b.es").Equals(Create("a@b.es")), "Two emails with the same value are equal");
        Assert.True(!Create("a@b.es").Equals(Create("c@d.es")), "Two emails with different values are not equal");
        Assert.Equal(Create("a@b.es").GetHashCode(), Create("a@b.es").GetHashCode(), "Hash code");
    }

    [Test]
    public static void TAC030_turns_event_setters_into_init() => Verify.Fix("""
        using Kernel;

        public sealed class OrderShipped : IDomainEvent
        {
            public int OrderId { get; set; }
            public string Carrier = "";
        }
        """, """
        using Kernel;

        public sealed class OrderShipped : IDomainEvent
        {
            public int OrderId { get; init; }
            public readonly string Carrier = "";
        }
        """);

    [Test]
    public static void Fix_all_applies_every_rule_in_one_pass() => Verify.FixAll("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; set; } = "";
            public List<string> Lines { get; } = new();

            public void Add(string line) => Lines.Add(line);
        }
        """, """
        using System.Collections.Generic;
        using Kernel;

        public sealed class Order : AggregateRoot<int>
        {
            public string Status { get; private set; } = "";
            private readonly List<string> _lines = new();
            public IReadOnlyList<string> Lines => _lines;

            private Order()
            {
            }

            public void Add(string line) => _lines.Add(line);
        }
        """, "TAC002", "TAC001", "TAC005");

    [Test]
    public static void Design_rules_have_no_automatic_fix() => Verify.Fix("""
        using Kernel;

        public sealed class Customer : AggregateRoot<int>
        {
            private Customer() { }
            public void Rename() { }
        }

        public sealed class Order : AggregateRoot<int>
        {
            public Customer Customer { get; private set; } = null!;
            private Order() { }
            public void Assign(Customer customer) => Customer = customer;
        }
        """, """
        using Kernel;

        public sealed class Customer : AggregateRoot<int>
        {
            private Customer() { }
            public void Rename() { }
        }

        public sealed class Order : AggregateRoot<int>
        {
            public Customer Customer { get; private set; } = null!;
            private Order() { }
            public void Assign(Customer customer) => Customer = customer;
        }
        """);
}
