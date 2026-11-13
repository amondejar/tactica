using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tactica;

/// <summary>The tactical DDD rules: aggregates, value objects, repositories and domain events.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TacticaAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] EqualityComponentMethods = ["GetEqualityComponents", "GetAtomicValues", "EqualityComponents"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.All);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var options = TacticaOptions.Read(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
            var model = new DomainModel(options);
            var reporter = new Reporter(Baseline.Read(start.Options.AdditionalFiles, start.CancellationToken), options.BaselineMode);

            start.RegisterSymbolStartAction(symbol => TypeStart(symbol, model, reporter), SymbolKind.NamedType);
            start.RegisterOperationAction(operation => RaisedOutside(operation, model, reporter), OperationKind.Invocation);
        });
    }

    private static void TypeStart(SymbolStartAnalysisContext context, DomainModel model, Reporter reporter)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var kind = model.Classify(type);

        if (model.IsRepository(type))
        {
            context.RegisterSymbolEndAction(end => Repository(end, type, model, reporter));
        }

        switch (kind)
        {
            case DomainKind.AggregateRoot or DomainKind.Entity:
                context.RegisterSymbolEndAction(end => Aggregate(end, type, kind, model, reporter));
                break;

            case DomainKind.ValueObject:
                var components = new ConcurrentBag<ISymbol>();
                var hasComponents = new StrongBox<bool>();
                context.RegisterOperationAction(
                    operation => ValueObjectMutated(operation, type, model, reporter),
                    OperationKind.SimpleAssignment, OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment,
                    OperationKind.Increment, OperationKind.Decrement);
                context.RegisterOperationBlockAction(block => CollectEqualityComponents(block, type, components, hasComponents));
                context.RegisterSymbolEndAction(end => ValueObject(end, type, model, reporter, components, hasComponents.Value));
                break;

            case DomainKind.DomainEvent:
                context.RegisterSymbolEndAction(end => DomainEvent(end, type, model, reporter));
                break;
        }
    }

    // ---------------------------------------------------------------- aggregates and entities

    private static void Aggregate(SymbolAnalysisContext context, INamedTypeSymbol type, DomainKind kind, DomainModel model, Reporter reporter)
    {
        var what = kind == DomainKind.AggregateRoot ? "the aggregate" : "the entity";
        var hint = model.Options.Hint(kind);
        var writableFromOutside = false;

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsImplicitlyDeclared || !property.ExplicitInterfaceImplementations.IsEmpty) continue;
            if (property.SetMethod is { } setter && IsVisibleOutside(setter.DeclaredAccessibility))
            {
                writableFromOutside = true;
                var accessor = setter.IsInitOnly ? "init" : "set";
                reporter.Report(context.ReportDiagnostic, Rules.PublicSetter, Location(setter, property), Reporter.Key(property),
                    $"{type.Name}.{property.Name} has {Accessibility(setter.DeclaredAccessibility)} {accessor}, so code outside {what} can change it without its rules. " +
                    $"Make it 'private set' and change {property.Name} in a method of {type.Name}, named after the domain operation, that checks them.{hint}");
            }
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared) continue;
            if (!IsVisibleOutside(field.DeclaredAccessibility)) continue;
            writableFromOutside = true;
            reporter.Report(context.ReportDiagnostic, Rules.PublicSetter, field.Locations[0], Reporter.Key(field),
                $"{type.Name}.{field.Name} is {Accessibility(field.DeclaredAccessibility)} field, so code outside {what} can change it without its rules. " +
                $"Make it private (or a property with 'private set') and change it in a method of {type.Name} that checks them.{hint}");
        }

        MutableCollections(context, type, kind, model, reporter);

        foreach (var member in DomainModel.State(type))
        {
            var referenced = DomainModel.Mentioned(DomainModel.TypeOf(member))
                .FirstOrDefault(t => model.Classify(t) == DomainKind.AggregateRoot);
            if (referenced is null) continue;

            var id = model.IdOf(referenced);
            var collection = !SymbolEqualityComparer.Default.Equals(referenced, DomainModel.TypeOf(member).WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            reporter.Report(context.ReportDiagnostic, Rules.AggregateReference, member.Locations[0], Reporter.Key(member),
                $"{type.Name}.{member.Name} holds the {referenced.Name} aggregate. Reference it by id instead " +
                $"({(collection ? $"a collection of {id}" : id)}), and load {referenced.Name} through its own repository when a use case needs it. " +
                $"Changing both in one operation is a sign the boundary is wrong; otherwise keep them consistent with a domain event.{hint}");
        }

        var state = DomainModel.State(type).ToList();
        if (!type.IsAbstract && type.TypeKind == TypeKind.Class && state.Count > 0)
        {
            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public) continue;
                if (constructor.Parameters.Any(p => !p.IsOptional && !p.IsParams)) continue;
                var location = constructor.IsImplicitlyDeclared ? type.Locations[0] : constructor.Locations[0];
                reporter.Report(context.ReportDiagnostic, Rules.PublicParameterlessConstructor, location, Reporter.Key(type),
                    $"{type.Name} has a public parameterless constructor{(constructor.IsImplicitlyDeclared ? " (the default one)" : "")}, so it can be created " +
                    $"without the values its rules need. Declare 'private {type.Name}() {{ }}' for the ORM, and create {type.Name} through a constructor " +
                    $"with parameters or a static factory (e.g. {type.Name}.Create(...)) that checks them.{hint}");
            }
        }

        if (kind == DomainKind.AggregateRoot && writableFromOutside && !HasBehavior(type))
        {
            reporter.Report(context.ReportDiagnostic, Rules.Anemic, type.Locations[0], Reporter.Key(type),
                $"{type.Name} has data but no behavior: its state is changed through setters, so the rules that should protect it live in other " +
                $"classes. Move each operation that changes {type.Name} into a method on it, named after what happens in the domain.{hint}");
        }
    }

    private static bool HasBehavior(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.MethodKind == MethodKind.Ordinary && !m.IsStatic && !m.IsOverride && !m.IsImplicitlyDeclared &&
            IsVisibleOutside(m.DeclaredAccessibility));

    // ---------------------------------------------------------------- value objects

    private static void ValueObject(
        SymbolAnalysisContext context, INamedTypeSymbol type, DomainModel model, Reporter reporter,
        ConcurrentBag<ISymbol> components, bool hasComponents)
    {
        var hint = model.Options.Hint(DomainKind.ValueObject);

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsImplicitlyDeclared || !property.ExplicitInterfaceImplementations.IsEmpty) continue;
            if (property.SetMethod is not { IsInitOnly: false } setter || !IsVisibleOutside(setter.DeclaredAccessibility)) continue;
            reporter.Report(context.ReportDiagnostic, Rules.MutableValueObject, Location(setter, property), Reporter.Key(property),
                $"{type.Name}.{property.Name} has {Accessibility(setter.DeclaredAccessibility)} set, but a value object never changes. " +
                $"Make it get-only (assigned in the constructor), and to change a value create a new {type.Name}.{hint}");
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared) continue;
            if (!IsVisibleOutside(field.DeclaredAccessibility)) continue;
            reporter.Report(context.ReportDiagnostic, Rules.MutableValueObject, field.Locations[0], Reporter.Key(field),
                $"{type.Name}.{field.Name} is a writable field, but a value object never changes. " +
                $"Make it a get-only property assigned in the constructor.{hint}");
        }

        MutableCollections(context, type, DomainKind.ValueObject, model, reporter);

        if (type.IsRecord || type.TypeKind == TypeKind.Struct || type.IsAbstract) return;

        if (!HasValueEquality(type))
        {
            reporter.Report(context.ReportDiagnostic, Rules.NoValueEquality, type.Locations[0], Reporter.Key(type),
                $"{type.Name} is a value object but is compared by reference: two {type.Name} objects with the same values are not equal. " +
                $"Make it a record (or a readonly record struct), or override Equals and GetHashCode over all its values.{hint}");
        }

        if (!hasComponents) return;
        var referenced = new HashSet<ISymbol>(components, SymbolEqualityComparer.Default);
        foreach (var member in DomainModel.State(type))
        {
            if (IsCovered(member, referenced)) continue;
            reporter.Report(context.ReportDiagnostic, Rules.EqualityComponentMissing, member.Locations[0], Reporter.Key(member),
                $"{type.Name}.{member.Name} is not part of the equality of {type.Name}: two values that differ only in {member.Name} compare equal. " +
                $"Return it from GetEqualityComponents(), or remove it if it is not part of the value.{hint}");
        }
    }

    private static bool HasValueEquality(INamedTypeSymbol type)
    {
        if (type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>" &&
                                        SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type)))
            return true;

        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.IsRecord) return true;
            if (current.GetMembers("Equals").OfType<IMethodSymbol>().Any(m =>
                    m.IsOverride && m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Object))
                return true;
        }

        return false;
    }

    private static void CollectEqualityComponents(
        OperationBlockAnalysisContext context, INamedTypeSymbol type, ConcurrentBag<ISymbol> components, StrongBox<bool> found)
    {
        if (context.OwningSymbol is not IMethodSymbol method || !EqualityComponentMethods.Contains(method.Name)) return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, type)) return;
        found.Value = true;

        foreach (var block in context.OperationBlocks)
        {
            foreach (var operation in block.DescendantsAndSelf())
            {
                if (operation is IMemberReferenceOperation { Member: IPropertySymbol or IFieldSymbol } reference)
                {
                    components.Add(reference.Member.OriginalDefinition);
                    // A computed property counts with everything its getter reads: Amount => _amount.
                    if (reference.Member is IPropertySymbol { GetMethod: { } getter })
                    {
                        foreach (var syntax in getter.DeclaringSyntaxReferences)
                        {
                            foreach (var name in syntax.GetSyntax(context.CancellationToken).DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                            {
                                foreach (var member in type.GetMembers(name.Identifier.ValueText)) components.Add(member);
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool IsCovered(ISymbol member, HashSet<ISymbol> referenced)
    {
        if (referenced.Contains(member)) return true;
        // An expression-bodied property declared as "=> field" is covered through its getter; and an auto-property through its backing field.
        return member is IPropertySymbol property && referenced.OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));
    }

    private static void ValueObjectMutated(OperationAnalysisContext context, INamedTypeSymbol type, DomainModel model, Reporter reporter)
    {
        var target = context.Operation switch
        {
            IAssignmentOperation assignment => assignment.Target,
            IIncrementOrDecrementOperation increment => increment.Target,
            _ => null,
        };

        if (target is not IMemberReferenceOperation { Member: IPropertySymbol or IFieldSymbol } reference) return;
        if (reference.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) return;
        if (!SymbolEqualityComparer.Default.Equals(reference.Member.ContainingType, type)) return;

        if (context.ContainingSymbol is IMethodSymbol method &&
            (method.MethodKind == MethodKind.Constructor || method.IsInitOnly ||
             (method.MethodKind == MethodKind.PropertySet && method.AssociatedSymbol is IPropertySymbol { SetMethod.IsInitOnly: true })))
            return;

        var where = context.ContainingSymbol.Name;
        reporter.Report(context.ReportDiagnostic, Rules.ValueObjectMutated, context.Operation.Syntax.GetLocation(),
            Reporter.Key(reference.Member) + " in " + Reporter.Key(context.ContainingSymbol),
            $"{where} changes {type.Name}.{reference.Member.Name} after the value was created, but a value object never changes: " +
            $"other objects sharing this {type.Name} would see the change. Return a new {type.Name} with the new value instead " +
            $"(e.g. 'return new {type.Name}(...)').{model.Options.Hint(DomainKind.ValueObject)}");
    }

    // ---------------------------------------------------------------- shared

    private static void MutableCollections(SymbolAnalysisContext context, INamedTypeSymbol type, DomainKind kind, DomainModel model, Reporter reporter)
    {
        var hint = model.Options.Hint(kind);
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            var (memberType, visible) = member switch
            {
                IPropertySymbol property when property.ExplicitInterfaceImplementations.IsEmpty =>
                    (property.Type, property.GetMethod is { } getter && IsVisibleOutside(getter.DeclaredAccessibility)),
                IFieldSymbol field => (field.Type, IsVisibleOutside(field.DeclaredAccessibility)),
                _ => (null, false),
            };

            if (memberType is null || !visible || !DomainModel.IsMutableCollection(memberType)) continue;

            var element = memberType is IArrayTypeSymbol array ? array.ElementType : ((INamedTypeSymbol)memberType).TypeArguments.LastOrDefault();
            var readOnly = element is null ? "IReadOnlyCollection" : $"IReadOnlyList<{element.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>";
            var advice = kind switch
            {
                DomainKind.ValueObject or DomainKind.DomainEvent =>
                    $"Expose it as {readOnly} and copy the items in the constructor, so the value cannot change after it is created.",
                _ =>
                    $"Keep the {memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} in a private field, expose it as {readOnly}, " +
                    $"and add methods to {type.Name} that change it and check its rules (e.g. Add{Singular(member.Name)}(...)).",
            };
            reporter.Report(context.ReportDiagnostic, Rules.MutableCollection, member.Locations[0], Reporter.Key(member),
                $"{type.Name}.{member.Name} exposes a {memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}, so callers can change its items " +
                $"without {type.Name} knowing. {advice}{hint}");
        }
    }

    // ---------------------------------------------------------------- repositories

    private static void Repository(SymbolAnalysisContext context, INamedTypeSymbol type, DomainModel model, Reporter reporter)
    {
        // An implementation is checked through its interface.
        if (type.TypeKind == TypeKind.Class && type.AllInterfaces.Any(model.IsRepository)) return;
        var hint = model.Options.RepositoryHint;

        var mentioned = new List<ITypeSymbol>();
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var contract in current.Interfaces) mentioned.AddRange(contract.TypeArguments.SelectMany(t => DomainModel.Mentioned(t)));
            if (current.BaseType is { IsGenericType: true } generic) mentioned.AddRange(generic.TypeArguments.SelectMany(t => DomainModel.Mentioned(t)));
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary || !IsVisibleOutside(method.DeclaredAccessibility)) continue;
            mentioned.AddRange(DomainModel.Mentioned(method.ReturnType));
            foreach (var parameter in method.Parameters) mentioned.AddRange(DomainModel.Mentioned(parameter.Type));

            if (DomainModel.Mentioned(method.ReturnType).Any(IsQueryable))
            {
                reporter.Report(context.ReportDiagnostic, Rules.QueryableRepository, method.Locations[0], Reporter.Key(method),
                    $"{type.Name}.{method.Name} returns an IQueryable, so callers can write any query and persistence details leak out of the " +
                    $"repository. Return the results (e.g. Task<IReadOnlyList<T>>) and add a method, or a specification, for each query the use cases need.{hint}");
            }
        }

        var reported = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var entity in mentioned)
        {
            if (model.Classify(entity) != DomainKind.Entity || !reported.Add(entity.OriginalDefinition)) continue;
            reporter.Report(context.ReportDiagnostic, Rules.RepositoryForEntity, type.Locations[0], Reporter.Key(type) + " " + entity.Name,
                $"{type.Name} loads or saves {entity.Name}, an entity inside an aggregate. Repositories work with whole aggregates: load the " +
                $"aggregate root that contains {entity.Name} and change {entity.Name} through it, so the root's rules always apply. If {entity.Name} " +
                $"really stands on its own, make it an aggregate root.{hint}");
        }
    }

    private static bool IsQueryable(ITypeSymbol type) =>
        DomainModel.FullName(type.OriginalDefinition) is "System.Linq.IQueryable" or "System.Linq.IOrderedQueryable" ||
        type.AllInterfaces.Any(i => DomainModel.FullName(i.OriginalDefinition) == "System.Linq.IQueryable");

    // ---------------------------------------------------------------- domain events

    private static void DomainEvent(SymbolAnalysisContext context, INamedTypeSymbol type, DomainModel model, Reporter reporter)
    {
        var hint = model.Options.Hint(DomainKind.DomainEvent);
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsImplicitlyDeclared || !property.ExplicitInterfaceImplementations.IsEmpty) continue;
            if (property.SetMethod is not { IsInitOnly: false } setter || !IsVisibleOutside(setter.DeclaredAccessibility)) continue;
            reporter.Report(context.ReportDiagnostic, Rules.MutableEvent, Location(setter, property), Reporter.Key(property),
                $"{type.Name}.{property.Name} has {Accessibility(setter.DeclaredAccessibility)} set, but a domain event records something that already " +
                $"happened and its handlers must all see the same data. Make it get-only or init, or declare {type.Name} as a positional record.{hint}");
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared || !IsVisibleOutside(field.DeclaredAccessibility)) continue;
            reporter.Report(context.ReportDiagnostic, Rules.MutableEvent, field.Locations[0], Reporter.Key(field),
                $"{type.Name}.{field.Name} is a writable field, but a domain event does not change after it is raised. Make it a get-only property.{hint}");
        }

        MutableCollections(context, type, DomainKind.DomainEvent, model, reporter);
    }

    private static void RaisedOutside(OperationAnalysisContext context, DomainModel model, Reporter reporter)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!model.Options.RaiseMethods.Contains(method.Name) || invocation.Instance is null) return;
        if (invocation.Instance is IInstanceReferenceOperation) return;

        var receiver = invocation.Instance.Type;
        var owner = method.ContainingType;

        // Code of the aggregate itself, such as a static factory raising "created" on the new instance.
        for (var inside = context.ContainingSymbol.ContainingType; inside is not null; inside = inside.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(inside.OriginalDefinition, receiver?.OriginalDefinition)) return;
        }

        var onAggregate = model.IsAggregateOrEntity(receiver) ||
                          (owner is not null && (model.IsAggregateOrEntity(owner) || model.Marker(owner) is DomainKind.AggregateRoot or DomainKind.Entity));
        if (!onAggregate) return;

        var name = receiver?.Name ?? owner!.Name;
        var raised = invocation.Arguments.FirstOrDefault()?.Value is { } argument
            ? (argument is IConversionOperation conversion ? conversion.Operand.Type : argument.Type)?.Name ?? "the event"
            : "the event";
        reporter.Report(context.ReportDiagnostic, Rules.EventRaisedOutside, invocation.Syntax.GetLocation(),
            Reporter.Key(context.ContainingSymbol) + " " + method.Name,
            $"{context.ContainingSymbol.Name} raises {raised} on a {name} from outside it. Raise it inside the {name} method that makes the change, " +
            $"so the event cannot be forgotten, duplicated or raised for a change that did not happen." +
            model.Options.Hint(DomainKind.DomainEvent));
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsVisibleOutside(Accessibility accessibility) =>
        accessibility is Microsoft.CodeAnalysis.Accessibility.Public or Microsoft.CodeAnalysis.Accessibility.Internal
            or Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal;

    /// <summary>"a public", "an internal".</summary>
    private static string Accessibility(Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "a public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "an internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "a protected internal",
        _ => "a non-private",
    };

    /// <summary>The setter when it is written out (its own location), else the property.</summary>
    private static Location Location(IMethodSymbol accessor, IPropertySymbol property) =>
        accessor.Locations.FirstOrDefault(l => l.IsInSource) ?? property.Locations[0];

    private static string Singular(string name) =>
        name.EndsWith("ies", StringComparison.Ordinal) ? name.Substring(0, name.Length - 3) + "y"
        : name.EndsWith("s", StringComparison.Ordinal) && name.Length > 1 ? name.Substring(0, name.Length - 1)
        : name;

    private sealed class StrongBox<T>
    {
        public T? Value;
    }
}
