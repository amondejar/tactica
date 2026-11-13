# Tactica

Roslyn analyzers for the **tactical patterns of Domain-Driven Design**. They check that aggregates guard their state,
that value objects are immutable and compared by value, that aggregates reference each other by id, that repositories
work with aggregate roots only, and that domain events are raised by their own aggregate.

They work with **your own base classes** (`AggregateRoot<TId>`, `Entity<TId>`, `ValueObject`, `IDomainEvent`…), with no
runtime dependency. Every warning says what to write instead, so a coding agent (Copilot, Claude Code, Cursor) can fix
it from the build output alone.

```text
Order.cs(12,33): warning TAC001: Order.Status has a public set, so code outside the aggregate can change it without its
rules. Make it 'private set' and change Status in a method of Order, named after the domain operation, that checks them.
Follow the pattern in src/Domain/Shipping/Shipment.cs.
```

## Why

Coding agents write DDD code that compiles and looks right but quietly breaks the model: a `public set` on an aggregate,
a `List<T>` anyone can add to, an `Order` that holds a `Customer` object, a value object with a member missing from
its equality. Code review catches some of it. Tactica catches it at build time, in the IDE and in CI, and tells the
agent how to fix it.

Layer and module rules (Domain must not reference Infrastructure, and so on) are a different job: use
[ArchitectureGuardian](https://github.com/kbourzayq/arch-guard) or NetArchTest for those. Tactica looks inside the
domain model.

## Install

```bash
dotnet add package Tactica
```

Add it to the projects that contain the domain model, or to all projects in `Directory.Build.props`:

```xml
<ItemGroup>
  <PackageReference Include="Tactica" Version="0.1.0-preview.1" PrivateAssets="all" />
</ItemGroup>
```

## Rules

| Rule | Checks | Severity |
|---|---|---|
| [TAC001](docs/rules.md#tac001) | Aggregate or entity state has a public or internal setter (or a writable public field) | Warning |
| [TAC002](docs/rules.md#tac002) | A domain object exposes a mutable collection (`List<T>`, `ICollection<T>`, arrays…) | Warning |
| [TAC003](docs/rules.md#tac003) | An aggregate or entity holds another aggregate instead of its id | Warning |
| [TAC005](docs/rules.md#tac005) | An aggregate or entity can be created with a public parameterless constructor | Warning |
| [TAC006](docs/rules.md#tac006) | An aggregate has data changed through setters and no behavior (anemic model) | Info |
| [TAC010](docs/rules.md#tac010) | A value object has a settable property or a writable field | Warning |
| [TAC011](docs/rules.md#tac011) | A value object's method changes its own state after creation | Warning |
| [TAC012](docs/rules.md#tac012) | A value object member is left out of `GetEqualityComponents()` | Warning |
| [TAC013](docs/rules.md#tac013) | A value object has no value equality (no record, `Equals` override or value-object base) | Warning |
| [TAC020](docs/rules.md#tac020) | A repository loads or saves an entity that is not an aggregate root | Warning |
| [TAC021](docs/rules.md#tac021) | A repository returns `IQueryable` | Warning |
| [TAC030](docs/rules.md#tac030) | A domain event has a settable property or a writable field | Warning |
| [TAC031](docs/rules.md#tac031) | A domain event is raised on an aggregate from outside it | Warning |

Change a severity, or turn a rule off, in `.editorconfig` as with any analyzer:

```ini
[*.cs]
dotnet_diagnostic.TAC006.severity = none
dotnet_diagnostic.TAC001.severity = error
```

## How it finds your model

A type is an **aggregate root**, **entity**, **value object** or **domain event** when it derives from, or implements,
a type with one of these names (in any namespace), or carries an attribute with one of these names:

| Kind | Recognized names |
|---|---|
| Aggregate root | `AggregateRoot`, `IAggregateRoot`, `Aggregate`, `AggregateRootBase`, `AggregateBase` |
| Entity | `Entity`, `IEntity`, `EntityBase`, `BaseEntity` |
| Value object | `ValueObject`, `IValueObject`, `ValueObjectBase`, `ScalarValueObject` |
| Domain event | `IDomainEvent`, `DomainEvent`, `DomainEventBase`, `BaseDomainEvent` |

That covers hand-written models and libraries such as Ardalis.SharedKernel and FunctionalDdd. A **repository** is a
type whose name ends with `Repository` or `Repositorio`.

## Configuration

Set MSBuild properties, for example in `Directory.Build.props`. Lists take `;` or `,` and add to the names above.

```xml
<PropertyGroup>
  <!-- Your own base types, by simple or full name. -->
  <TacticaAggregateRoots>Acme.Domain.AggregateRoot</TacticaAggregateRoots>
  <TacticaEntities>Acme.Domain.Entity</TacticaEntities>
  <TacticaValueObjects>Acme.Domain.ValueObject</TacticaValueObjects>
  <TacticaDomainEvents>Acme.Domain.IDomainEvent</TacticaDomainEvents>
  <TacticaRepositorySuffixes>Repository;Store</TacticaRepositorySuffixes>
  <!-- Methods that raise a domain event (default: Raise, AddDomainEvent, RegisterDomainEvent, RaiseDomainEvent, AddEvent, RecordEvent, Emit). -->
  <TacticaRaiseMethods>Publish</TacticaRaiseMethods>
  <!-- Only your names, not the recognized ones. -->
  <TacticaDefaultConventions>false</TacticaDefaultConventions>

  <!-- The file each message points to: the best example of the pattern in this repository. -->
  <TacticaExampleAggregate>src/Domain/Orders/Order.cs</TacticaExampleAggregate>
  <TacticaExampleValueObject>src/Domain/Money.cs</TacticaExampleValueObject>
  <TacticaExampleDomainEvent>src/Domain/Orders/OrderPlaced.cs</TacticaExampleDomainEvent>
  <TacticaExampleRepository>src/Application/IOrderRepository.cs</TacticaExampleRepository>
</PropertyGroup>
```

The same settings work in a `.globalconfig` file, as `tactica.aggregate_roots`, `tactica.entities`,
`tactica.value_objects`, `tactica.domain_events`, `tactica.repository_suffixes`, `tactica.raise_methods`,
`tactica.default_conventions`, `tactica.example_aggregate`, `tactica.example_value_object`,
`tactica.example_domain_event` and `tactica.example_repository`.

## Existing code: the baseline

On a code base that did not follow these rules, accept today's findings and let the build report only new ones:

```bash
dotnet tool install --global Tactica.Tool    # or as a local tool
dotnet tactica baseline                      # builds, then writes tactica.baseline at the repository root
```

Commit `tactica.baseline`. Each line is a rule and a symbol (`TAC001 Shop.Billing.Invoice.Total`), so it survives
edits elsewhere in the file. Fix findings over time and run `dotnet tactica baseline` again to remove them.

## For coding agents

Add this to your `AGENTS.md`, `CLAUDE.md` or `.github/copilot-instructions.md`:

```markdown
## Domain model

The domain model follows the tactical DDD patterns and the build checks them with Tactica (warnings TAC0xx).
Fix every TAC warning in code you touch: the message says what to write instead. Do not suppress them, and do not
add entries to tactica.baseline.
```

## Tested on real code

Run on a production DDD solution (330 files, 18 aggregates, 11 value objects, 17 repositories, written to these
patterns): **no false positives**. Six typical mistakes injected into it (a public setter, an exposed `List<T>`, an
aggregate held by another, a member missing from equality, a repository returning `IQueryable`, a mutable event) were
all reported. `samples/Shop` shows the common mistakes next to a well-formed model; `tests/e2e/run.sh` builds it with the package.

## Build

```bash
dotnet run --project tests/Tactica.Tests     # rule tests
tests/e2e/run.sh                             # pack, build the sample, baseline
```

Without access to nuget.org, add `-p:TacticaOfflineRoslyn=true` to build against the Roslyn assemblies of the SDK.

## License

MIT
