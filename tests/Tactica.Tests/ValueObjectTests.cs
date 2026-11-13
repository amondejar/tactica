namespace Tactica.Tests;

public static class ValueObjectTests
{
    [Test]
    public static void A_well_formed_value_object_has_no_findings() => Verify.Diagnostics("""
        using System;
        using System.Collections.Generic;
        using Kernel;

        public sealed class Weight : ValueObject
        {
            public decimal Kilograms { get; }
            private Weight(decimal kilograms) => Kilograms = kilograms;
            public static Weight FromKilograms(decimal kilograms) =>
                kilograms < 0 ? throw new ArgumentOutOfRangeException(nameof(kilograms)) : new Weight(kilograms);
            public Weight Add(Weight other) => new(Kilograms + other.Kilograms);
            protected override IEnumerable<object?> GetEqualityComponents() { yield return Kilograms; }
        }

        public sealed record Money(decimal Amount, string Currency) : IValueObject;
        public interface IValueObject { }
        public readonly record struct Percentage(decimal Value) : IValueObject;
        """);

    [Test]
    public static void Settable_properties_and_writable_fields_are_reported() => Verify.Diagnostics("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class Address : ValueObject
        {
            public string Street { get; {|TAC010:set|}; } = "";
            public string City { get; internal {|TAC010:set|}; } = "";
            public string Country { get; init; } = "";
            public string {|TAC010:Zip|} = "";
            public readonly string Region = "";
            protected override IEnumerable<object?> GetEqualityComponents()
            {
                yield return Street; yield return City; yield return Country; yield return Zip; yield return Region;
            }
        }
        """);

    [Test]
    public static void Changing_a_value_after_creation_is_reported()
    {
        var diagnostics = Verify.Diagnostics("""
            using System.Collections.Generic;
            using Kernel;

            public sealed class Counter : ValueObject
            {
                private int _hits;
                public int Value { get; private set; }
                public Counter(int value) { Value = value; _hits = 0; }
                public void Increment() { {|TAC011:Value++|}; {|TAC011:_hits += 1|}; }
                public void Reset() => {|TAC011:this.Value = 0|};
                public Counter Next() => new(Value + 1);
                public static Counter Copy(Counter other) { var copy = new Counter(0); return copy; }
                protected override IEnumerable<object?> GetEqualityComponents() { yield return Value; yield return _hits; }
            }
            """);

        Assert.Contains("Increment changes Counter.Value after the value was created", diagnostics[0].GetMessage());
        Assert.Contains("Return a new Counter", diagnostics[0].GetMessage());
    }

    [Test]
    public static void Members_left_out_of_the_equality_components_are_reported() => Verify.Diagnostics("""
        using System.Collections.Generic;
        using Kernel;

        public sealed class Money : ValueObject
        {
            private readonly string _currency;
            public decimal Amount { get; }
            public string {|TAC012:Symbol|} { get; }
            private readonly int {|TAC012:_scale|};
            public string Currency => _currency;
            public string Display => $"{Amount} {Symbol}";
            public Money(decimal amount, string currency, string symbol, int scale)
            {
                Amount = amount; _currency = currency; Symbol = symbol; _scale = scale;
            }
            protected override IEnumerable<object?> GetEqualityComponents()
            {
                yield return Amount;
                yield return Currency;
            }
        }
        """);

    [Test]
    public static void Value_objects_without_value_equality_are_reported() => Verify.Diagnostics("""
        using System;

        public interface IValueObject { }

        public sealed class {|TAC013:Email|} : IValueObject
        {
            public string Value { get; }
            public Email(string value) => Value = value;
        }

        public sealed class Phone : IValueObject, IEquatable<Phone>
        {
            public string Value { get; }
            public Phone(string value) => Value = value;
            public bool Equals(Phone? other) => other is not null && other.Value == Value;
        }

        public sealed class Url : IValueObject
        {
            public string Value { get; }
            public Url(string value) => Value = value;
            public override bool Equals(object? obj) => obj is Url other && other.Value == Value;
            public override int GetHashCode() => Value.GetHashCode();
        }

        public sealed record Name(string Value) : IValueObject;
        """, kernel: false);

    [Test]
    public static void Value_objects_expose_collections_read_only()
    {
        var message = Verify.Diagnostics("""
            using System.Collections.Generic;
            using System.Linq;
            using Kernel;

            public sealed class Tags : ValueObject
            {
                public List<string> {|TAC002:Items|} { get; }
                public Tags(IEnumerable<string> items) => Items = items.ToList();
                protected override IEnumerable<object?> GetEqualityComponents() => Items;
            }
            """).Single().GetMessage();

        Assert.Contains("copy the items in the constructor", message);
    }
}
