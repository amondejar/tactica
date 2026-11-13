using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tactica;

/// <summary>What a type is in the domain model.</summary>
internal enum DomainKind
{
    None,
    AggregateRoot,
    Entity,
    ValueObject,
    DomainEvent,
}

/// <summary>
/// The settings, from MSBuild properties (<c>TacticaAggregateRoots</c>…) or a global analyzer config
/// (<c>tactica.aggregate_roots = …</c>).
/// </summary>
internal sealed class TacticaOptions
{
    // Base types and interfaces of the common DDD libraries and of hand-written models (Ardalis.SharedKernel,
    // FunctionalDdd, eShop and most templates). A configured name is added to these.
    private static readonly string[] DefaultAggregateRoots = ["AggregateRoot", "IAggregateRoot", "Aggregate", "AggregateRootBase", "AggregateBase"];
    private static readonly string[] DefaultEntities = ["Entity", "IEntity", "EntityBase", "BaseEntity"];
    private static readonly string[] DefaultValueObjects = ["ValueObject", "IValueObject", "ValueObjectBase", "ScalarValueObject"];
    private static readonly string[] DefaultDomainEvents = ["IDomainEvent", "DomainEvent", "DomainEventBase", "BaseDomainEvent"];
    private static readonly string[] DefaultRepositorySuffixes = ["Repository", "Repositorio"];
    private static readonly string[] DefaultRaiseMethods = ["Raise", "RaiseDomainEvent", "AddDomainEvent", "RegisterDomainEvent", "AddEvent", "RecordEvent", "Emit"];

    public HashSet<string> AggregateRoots { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Entities { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ValueObjects { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DomainEvents { get; } = new(StringComparer.Ordinal);
    public List<string> RepositorySuffixes { get; } = [];
    public HashSet<string> RaiseMethods { get; } = new(StringComparer.Ordinal);
    public Dictionary<DomainKind, string> Examples { get; } = [];
    public string? RepositoryExample { get; private set; }
    public bool BaselineMode { get; private set; }

    public static TacticaOptions Read(AnalyzerConfigOptions options)
    {
        var result = new TacticaOptions();
        var conventions = !string.Equals(Get(options, "default_conventions", "DefaultConventions"), "false", StringComparison.OrdinalIgnoreCase);

        Fill(result.AggregateRoots, conventions ? DefaultAggregateRoots : [], Get(options, "aggregate_roots", "AggregateRoots"));
        Fill(result.Entities, conventions ? DefaultEntities : [], Get(options, "entities", "Entities"));
        Fill(result.ValueObjects, conventions ? DefaultValueObjects : [], Get(options, "value_objects", "ValueObjects"));
        Fill(result.DomainEvents, conventions ? DefaultDomainEvents : [], Get(options, "domain_events", "DomainEvents"));
        Fill(result.RaiseMethods, DefaultRaiseMethods, Get(options, "raise_methods", "RaiseMethods"));

        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        Fill(suffixes, DefaultRepositorySuffixes, Get(options, "repository_suffixes", "RepositorySuffixes"));
        result.RepositorySuffixes.AddRange(suffixes);

        Example(DomainKind.AggregateRoot, "example_aggregate", "ExampleAggregate");
        Example(DomainKind.ValueObject, "example_value_object", "ExampleValueObject");
        Example(DomainKind.DomainEvent, "example_domain_event", "ExampleDomainEvent");
        result.RepositoryExample = Get(options, "example_repository", "ExampleRepository");
        result.BaselineMode = string.Equals(Get(options, "baseline_mode", "BaselineMode"), "true", StringComparison.OrdinalIgnoreCase);
        return result;

        void Example(DomainKind kind, string key, string property)
        {
            if (Get(options, key, property) is { } path) result.Examples[kind] = path;
        }
    }

    /// <summary>"Follow the pattern in …", when an example file is configured for this kind of object.</summary>
    public string Hint(DomainKind kind)
    {
        if (kind == DomainKind.Entity) kind = DomainKind.AggregateRoot;
        return Examples.TryGetValue(kind, out var path) ? $" Follow the pattern in {path}." : "";
    }

    public string RepositoryHint => RepositoryExample is { } path ? $" Follow the pattern in {path}." : "";

    private static string? Get(AnalyzerConfigOptions options, string key, string property)
    {
        if (options.TryGetValue("tactica." + key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (options.TryGetValue("build_property.Tactica" + property, out value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static void Fill(HashSet<string> target, IEnumerable<string> defaults, string? configured)
    {
        foreach (var name in defaults) target.Add(name);
        if (configured is null) return;
        foreach (var name in configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0) target.Add(StripGenerics(trimmed));
        }
    }

    /// <summary><c>AggregateRoot&lt;TId&gt;</c> and <c>AggregateRoot`1</c> both mean <c>AggregateRoot</c>.</summary>
    private static string StripGenerics(string name)
    {
        var cut = name.IndexOfAny(['<', '`']);
        return cut < 0 ? name : name.Substring(0, cut);
    }
}

/// <summary>Classifies types into aggregate roots, entities, value objects and domain events.</summary>
internal sealed class DomainModel(TacticaOptions options)
{
    private readonly ConcurrentDictionary<ITypeSymbol, DomainKind> _kinds = new(SymbolEqualityComparer.Default);

    public TacticaOptions Options { get; } = options;

    public DomainKind Classify(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return DomainKind.None;
        return _kinds.GetOrAdd(named.OriginalDefinition, t => Compute((INamedTypeSymbol)t));
    }

    public bool IsAggregateOrEntity(ITypeSymbol? type) => Classify(type) is DomainKind.AggregateRoot or DomainKind.Entity;

    /// <summary>The base types and interfaces that mark the others (AggregateRoot&lt;TId&gt; itself, IDomainEvent…).</summary>
    public DomainKind Marker(ITypeSymbol type)
    {
        if (Matches(type, Options.AggregateRoots)) return DomainKind.AggregateRoot;
        if (Matches(type, Options.Entities)) return DomainKind.Entity;
        if (Matches(type, Options.ValueObjects)) return DomainKind.ValueObject;
        if (Matches(type, Options.DomainEvents)) return DomainKind.DomainEvent;
        return DomainKind.None;
    }

    private DomainKind Compute(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return DomainKind.None;
        if (Marker(type) != DomainKind.None) return DomainKind.None;

        var found = DomainKind.None;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (!SymbolEqualityComparer.Default.Equals(current, type)) found = Best(found, Marker(current));
            foreach (var attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass is { } attributeClass) found = Best(found, AttributeMarker(attributeClass));
            }
        }

        foreach (var contract in type.AllInterfaces) found = Best(found, Marker(contract));
        return found;
    }

    private DomainKind AttributeMarker(INamedTypeSymbol attribute)
    {
        var name = attribute.Name.EndsWith("Attribute", StringComparison.Ordinal)
            ? attribute.Name.Substring(0, attribute.Name.Length - "Attribute".Length)
            : attribute.Name;
        if (Options.AggregateRoots.Contains(name)) return DomainKind.AggregateRoot;
        if (Options.Entities.Contains(name)) return DomainKind.Entity;
        if (Options.ValueObjects.Contains(name)) return DomainKind.ValueObject;
        if (Options.DomainEvents.Contains(name)) return DomainKind.DomainEvent;
        return DomainKind.None;
    }

    /// <summary>An aggregate root is also an entity: the more specific kind wins.</summary>
    private static DomainKind Best(DomainKind a, DomainKind b)
    {
        if (a == DomainKind.None) return b;
        if (b == DomainKind.None) return a;
        return Rank(a) <= Rank(b) ? a : b;

        static int Rank(DomainKind kind) => kind switch
        {
            DomainKind.AggregateRoot => 0,
            DomainKind.Entity => 1,
            DomainKind.ValueObject => 2,
            _ => 3,
        };
    }

    private static bool Matches(ITypeSymbol type, HashSet<string> names) =>
        names.Contains(type.Name) || names.Contains(FullName(type));

    public static string FullName(ITypeSymbol type)
    {
        var name = type.Name;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType) name = outer.Name + "." + name;
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
    }

    /// <summary>A repository: its name ends with a repository suffix (IOrderRepository, PedidoRepositorio).</summary>
    public bool IsRepository(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Interface or TypeKind.Class) || type.IsStatic) return false;
        foreach (var suffix in Options.RepositorySuffixes)
        {
            if (!type.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            // IRepository<T> and RepositoryBase<T> are the generic bases, not repositories of anything yet.
            var stem = type.Name.Substring(0, type.Name.Length - suffix.Length);
            return stem is not ("" or "I" or "Base" or "IBase" or "Generic" or "IGeneric" or "IRead" or "Read" or "EfCore" or "Ef");
        }

        return false;
    }

    /// <summary>The type and, for generic types, its type arguments: List&lt;Order&gt;, Task&lt;Order?&gt;, Dictionary&lt;Guid, Order&gt;.</summary>
    public static IEnumerable<ITypeSymbol> Mentioned(ITypeSymbol type, int depth = 0)
    {
        yield return type;
        if (depth > 3) yield break;
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var inner in Mentioned(array.ElementType, depth + 1)) yield return inner;
                break;
            case INamedTypeSymbol { IsGenericType: true } named:
                foreach (var argument in named.TypeArguments)
                {
                    foreach (var inner in Mentioned(argument, depth + 1)) yield return inner;
                }

                break;
        }
    }

    private static readonly HashSet<string> MutableCollections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List", "System.Collections.Generic.IList", "System.Collections.Generic.ICollection",
        "System.Collections.Generic.ISet", "System.Collections.Generic.HashSet", "System.Collections.Generic.SortedSet",
        "System.Collections.Generic.Dictionary", "System.Collections.Generic.IDictionary", "System.Collections.Generic.SortedDictionary",
        "System.Collections.Generic.SortedList", "System.Collections.Generic.LinkedList", "System.Collections.Generic.Queue",
        "System.Collections.Generic.Stack", "System.Collections.ObjectModel.Collection", "System.Collections.ObjectModel.ObservableCollection",
        "System.Collections.ArrayList", "System.Collections.Hashtable", "System.Collections.IList", "System.Collections.IDictionary",
    };

    /// <summary>
    /// A collection callers can change: List&lt;T&gt;, ICollection&lt;T&gt;, Dictionary… and arrays, except byte[] (binary
    /// content such as a file or a row version, which is stored rather than edited).
    /// </summary>
    public static bool IsMutableCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType.SpecialType != SpecialType.System_Byte;
        return type is INamedTypeSymbol named && MutableCollections.Contains(FullName(named.OriginalDefinition));
    }

    /// <summary>
    /// The state of a type: its instance fields, with each auto-property standing for its hidden backing field.
    /// </summary>
    public static IEnumerable<ISymbol> State(INamedTypeSymbol type)
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst) continue;
            if (field.IsImplicitlyDeclared)
            {
                if (field.AssociatedSymbol is IPropertySymbol property) yield return property;
            }
            else
            {
                yield return field;
            }
        }
    }

    public static ITypeSymbol TypeOf(ISymbol member) => member switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    /// <summary>The id type of an aggregate deriving from a generic base such as AggregateRoot&lt;OrderId&gt;.</summary>
    public string IdOf(ITypeSymbol aggregate)
    {
        for (var current = aggregate.BaseType; current is not null; current = current.BaseType)
        {
            if (Marker(current) is DomainKind.AggregateRoot or DomainKind.Entity && current.TypeArguments.Length == 1)
                return current.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        return aggregate.Name + "Id";
    }
}
