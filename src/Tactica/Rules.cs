using Microsoft.CodeAnalysis;

namespace Tactica;

/// <summary>
/// The Tactica rules. Messages are written for whoever fixes the code, often a coding agent: each one says what is wrong
/// in this model and what to write instead.
/// </summary>
internal static class Rules
{
    private const string Aggregates = "Tactica.Aggregates";
    private const string ValueObjects = "Tactica.ValueObjects";
    private const string Repositories = "Tactica.Repositories";
    private const string Events = "Tactica.DomainEvents";

    public static readonly DiagnosticDescriptor PublicSetter = Rule(
        "TAC001", Aggregates, "Aggregate state can be changed from outside",
        "An aggregate (or one of its entities) changes only through its own methods, which check its rules. A public or internal setter lets any code skip them.");

    public static readonly DiagnosticDescriptor MutableCollection = Rule(
        "TAC002", Aggregates, "Domain object exposes a mutable collection",
        "Callers can add or remove items without going through the domain object. Expose IReadOnlyList<T> or IReadOnlyCollection<T> over a private field and change it through methods.");

    public static readonly DiagnosticDescriptor AggregateReference = Rule(
        "TAC003", Aggregates, "Aggregate holds another aggregate",
        "An aggregate is a consistency boundary: it references other aggregates by id, never by object, so that one transaction changes one aggregate.");

    public static readonly DiagnosticDescriptor PublicParameterlessConstructor = Rule(
        "TAC005", Aggregates, "Aggregate can be created without its invariants",
        "A public parameterless constructor creates the object in a state its rules may not allow. Keep it private for the ORM and create the object through a constructor or factory that checks them.");

    public static readonly DiagnosticDescriptor Anemic = Rule(
        "TAC006", Aggregates, "Aggregate has data but no behavior",
        "Other code changes this aggregate through its setters, so its rules live outside it. Move the operations into methods on the aggregate.",
        DiagnosticSeverity.Info);

    public static readonly DiagnosticDescriptor MutableValueObject = Rule(
        "TAC010", ValueObjects, "Value object can be changed",
        "A value object is immutable: to change a value, create a new one. Settable properties and writable fields let shared values change under their owners.");

    public static readonly DiagnosticDescriptor ValueObjectMutated = Rule(
        "TAC011", ValueObjects, "Value object changes after it is created",
        "A value object is immutable: methods that change a value return a new instance instead of assigning to this one.");

    public static readonly DiagnosticDescriptor EqualityComponentMissing = Rule(
        "TAC012", ValueObjects, "Member left out of value object equality",
        "Two value objects are equal when all their values are. A member that is not part of the equality components makes different values compare equal.");

    public static readonly DiagnosticDescriptor NoValueEquality = Rule(
        "TAC013", ValueObjects, "Value object compared by reference",
        "A value object is equal to another with the same values. Without an Equals override (or a record, or a value-object base class) it is compared by reference.");

    public static readonly DiagnosticDescriptor RepositoryForEntity = Rule(
        "TAC020", Repositories, "Repository for an entity that is not an aggregate root",
        "Repositories load and save whole aggregates. Loading an inner entity on its own skips the rules of its aggregate root.");

    public static readonly DiagnosticDescriptor QueryableRepository = Rule(
        "TAC021", Repositories, "Repository returns IQueryable",
        "An IQueryable lets callers build any query, so persistence details leak out of the repository and its queries cannot be found in one place.");

    public static readonly DiagnosticDescriptor MutableEvent = Rule(
        "TAC030", Events, "Domain event can be changed",
        "A domain event records something that happened: its data does not change after it is raised. Use get-only or init properties, or a positional record.");

    public static readonly DiagnosticDescriptor EventRaisedOutside = Rule(
        "TAC031", Events, "Domain event raised from outside its aggregate",
        "The aggregate raises its events inside the method that makes the change, so an event is never forgotten, duplicated or raised for a change that did not happen.");

    public static readonly DiagnosticDescriptor[] All =
    [
        PublicSetter, MutableCollection, AggregateReference, PublicParameterlessConstructor, Anemic,
        MutableValueObject, ValueObjectMutated, EqualityComponentMissing, NoValueEquality,
        RepositoryForEntity, QueryableRepository,
        MutableEvent, EventRaisedOutside,
    ];

    private static DiagnosticDescriptor Rule(
        string id, string category, string title, string description, DiagnosticSeverity severity = DiagnosticSeverity.Warning) =>
        new(id, title, "{0}", category, severity, isEnabledByDefault: true, description,
            helpLinkUri: $"https://github.com/amondejar/tactica/blob/main/docs/rules.md#{id.ToLowerInvariant()}");
}
