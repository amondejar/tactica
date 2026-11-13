# Tactica rules

Each rule applies to the kinds of type it names. See [How it finds your model](../README.md#how-it-finds-your-model)
for how a type is recognized as an aggregate root, entity, value object, domain event or repository.

Rules with an automatic fix offer it in the IDE light bulb and in `dotnet format analyzers --diagnostics <ids>`.

To suppress one finding, use `#pragma warning disable TAC001` or `[SuppressMessage("Tactica", "TAC001")]` with a
justification. To accept all current findings, see [the baseline](../README.md#existing-code-the-baseline).

## TAC001

**Aggregate state can be changed from outside.** Aggregate roots and entities.

A property with a `public`, `internal` or `protected internal` setter (`set` or `init`), or a writable non-private
field. Any code can then change the aggregate without the rules its methods enforce.

```csharp
public sealed class Order : AggregateRoot<OrderId>
{
    public OrderStatus Status { get; set; }          // TAC001
}
```

Instead:

```csharp
public sealed class Order : AggregateRoot<OrderId>
{
    public OrderStatus Status { get; private set; }

    public void Submit()
    {
        if (_lines.Count == 0) throw new DomainException("An order needs at least one line.");
        Status = OrderStatus.Submitted;
    }
}
```

`protected set` and `private set` are fine. ORMs such as EF Core set private setters and backing fields.

**Automatic fix:** makes the setter `private set` (an `init` becomes `private set`), or the field `private`.

## TAC002

**Domain object exposes a mutable collection.** Aggregate roots, entities, value objects and domain events.

A non-private property or field of type `List<T>`, `IList<T>`, `ICollection<T>`, `ISet<T>`, `HashSet<T>`,
`Dictionary<K,V>`, `IDictionary<K,V>`, `Collection<T>`, `ObservableCollection<T>`, other mutable collections, or an array.
`byte[]` is not reported: binary content such as a file or a row version is stored, not edited item by item.

```csharp
public List<OrderLine> Lines { get; } = new();       // TAC002: order.Lines.Add(...) skips the rules
```

Instead:

```csharp
private readonly List<OrderLine> _lines = new();
public IReadOnlyList<OrderLine> Lines => _lines;

public void AddLine(Product product, int quantity) { /* check, then */ _lines.Add(...); }
```

For value objects and domain events, expose `IReadOnlyList<T>` and copy the items in the constructor.

**Automatic fix:** moves the collection to a `private readonly` field (`_lines`), exposes the read-only view, and makes every use inside the type use the field. The field stays writable if a method other than a constructor replaces the collection. Arrays become `IReadOnlyList<T>`, sets and other collections `IReadOnlyCollection<T>`, dictionaries `IReadOnlyDictionary<K,V>`. The methods that change the collection are yours to write.

## TAC003

**Aggregate holds another aggregate.** Aggregate roots and entities.

A field or property whose type is another aggregate root, alone or in a collection (`Customer`, `Customer?`,
`List<Customer>`). Aggregates are consistency boundaries: one transaction changes one aggregate, and holding the other
one invites changing both, loading big object graphs, and lazy-loading surprises.

```csharp
public Customer Customer { get; private set; }       // TAC003
```

Instead, reference it by id and load it through its repository when a use case needs it:

```csharp
public CustomerId CustomerId { get; private set; }
```

If two aggregates must always change together, the boundary may be wrong. Otherwise keep them consistent with a domain
event.

**No automatic fix:** changing the type to an id changes every use and how the aggregates are loaded.

## TAC005

**Aggregate can be created without its invariants.** Non-abstract aggregate roots and entities with state.

A `public` parameterless constructor, including the default one the compiler adds when the class declares none. It
creates the object in a state its rules may not allow.

Instead, keep a `private` parameterless constructor for the ORM and create the object through a constructor with
parameters, or a static factory, that checks them:

```csharp
private Order() { }
public static Order Place(CustomerId customer) { ... }
```

**Automatic fix:** makes an explicit constructor `private`, or adds `private Order() { }` after the fields and properties.

## TAC006

**Aggregate has data but no behavior.** Aggregate roots. Severity: Info.

An aggregate whose state other code changes through its setters (TAC001), and that has no public or internal instance
methods. Its rules then live in services and handlers instead of in the aggregate. Move each operation that changes it
into a method named after what happens in the domain.

**No automatic fix.**

## TAC010

**Value object can be changed.** Value objects.

A property with a non-private `set`, or a writable non-private field. A value object is immutable: to change a value,
create a new one. `init` is allowed.

```csharp
public sealed class Money : ValueObject
{
    public decimal Amount { get; set; }              // TAC010
}
```

Instead: `public decimal Amount { get; }`, assigned in the constructor.

**Automatic fix:** removes the setter of an auto-property (assign it in the constructor), or makes the field `readonly`.

## TAC011

**Value object changes after it is created.** Value objects.

An assignment, `++`, `--` or compound assignment to the value object's own field or property in any method other than a
constructor or an `init` accessor. Other objects that share the value would see it change.

```csharp
public void Add(decimal amount) => Amount += amount;           // TAC011
```

Instead: `public Money Add(decimal amount) => new(Amount + amount, Currency);`

**No automatic fix:** the method has to return a new value, which changes its signature and its callers.

## TAC012

**Member left out of value object equality.** Value objects that override `GetEqualityComponents()` (also
`GetAtomicValues()` or `EqualityComponents()`).

An instance field or auto-property that the method does not return, directly or through a property that reads it.
Two values that differ only in that member compare equal. It usually happens when a member is added later.

Return it from the method, or remove it if it is not part of the value. If it is deliberately excluded (a cached
value, for example), suppress the finding with a justification.

**Automatic fix:** adds `yield return Member;` at the end of an iterator `GetEqualityComponents()`. Other shapes of the method are left for you.

## TAC013

**Value object compared by reference.** Non-abstract value object classes that are not records.

No `Equals(object)` override in the class or its base classes, no `IEquatable<T>` implementation, and no record base.
Two instances with the same values are then not equal. Make it a `record` (or `readonly record struct`), derive from a
value-object base class, or override `Equals` and `GetHashCode`.

**Automatic fix:** generates `Equals(object?)` (comparing every field and auto-property with `Equals(a, b)`) and `GetHashCode()` with `HashCode.Combine`.

## TAC020

**Repository for an entity that is not an aggregate root.** Repositories.

A repository whose public methods (or generic base, such as `IRepository<OrderLine>`) take or return an entity that is
not an aggregate root. Loading an inner entity on its own skips its root's rules.

Load the aggregate root that contains the entity and change the entity through it. If the entity really stands on its
own, make it an aggregate root. Implementations of a repository interface are checked through the interface.

**No automatic fix.**

## TAC021

**Repository returns IQueryable.** Repositories.

A public method returning `IQueryable<T>` (or `Task<IQueryable<T>>`). Callers can compose any query, so persistence
details leak out and the queries the domain needs cannot be found in one place. Return results
(`Task<IReadOnlyList<Order>>`) and add a method, or a specification, per query.

**No automatic fix.**

## TAC030

**Domain event can be changed.** Domain events.

A property with a non-private, non-`init` setter, or a writable non-private field. An event records something that
already happened, and all its handlers must see the same data.

Instead: `public sealed record OrderPlaced(OrderId OrderId, DateTime OccurredOn) : IDomainEvent;`

**Automatic fix:** turns `set` into `init`, or makes the field `readonly`.

## TAC031

**Domain event raised from outside its aggregate.** Calls to a raise method (`Raise`, `AddDomainEvent`,
`RegisterDomainEvent`, `RaiseDomainEvent`, `AddEvent`, `RecordEvent`, `Emit`, or `TacticaRaiseMethods`) on an aggregate
or entity, from code outside that type.

```csharp
// In a handler:
order.Submit();
order.AddDomainEvent(new OrderSubmitted(order.Id));  // TAC031
```

Instead, raise it inside `Order.Submit()`, so the event cannot be forgotten, duplicated, or raised for a change that
did not happen. Keeping the raise method `protected` prevents this at compile time.

**No automatic fix:** the event has to move into the aggregate method that makes the change.
