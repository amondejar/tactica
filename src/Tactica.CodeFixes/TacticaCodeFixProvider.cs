using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tactica.CodeFixes;

/// <summary>
/// The mechanical fixes. Rules whose fix changes the design (reference by id, move an event, split a repository) have
/// none: their message says what to do.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(TacticaCodeFixProvider)), Shared]
public sealed class TacticaCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create("TAC001", "TAC002", "TAC005", "TAC010", "TAC012", "TAC013", "TAC030");

    /// <summary>
    /// Fix all (the IDE's "fix all occurrences" and <c>dotnet format analyzers</c>): applies the fixes of a document one
    /// after another, from its end to its start, so each finding's position is still valid when its turn comes.
    /// </summary>
    public override FixAllProvider GetFixAllProvider() => FixAllProvider.Create(FixDocumentAsync);

    private static async Task<Document?> FixDocumentAsync(FixAllContext context, Document document, ImmutableArray<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.OrderByDescending(d => d.Location.SourceSpan.Start))
        {
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || diagnostic.Location.SourceSpan.End > root.FullSpan.End) continue;
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var action = Fix(diagnostic.Id, document, root, node);
            // Each finding has exactly one fix, so the fix is applied whatever equivalence key the caller asked for:
            // dotnet format asks for one provider's findings of several rules under the key of the first one.
            if (action is null) continue;

            foreach (var operation in await action.GetOperationsAsync(context.CancellationToken).ConfigureAwait(false))
            {
                if (operation is ApplyChangesOperation apply && apply.ChangedSolution.GetDocument(document.Id) is { } changed) document = changed;
            }
        }

        return document;
    }

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (Fix(diagnostic.Id, context.Document, root, node) is { } action) context.RegisterCodeFix(action, diagnostic);
        }
    }

    private static CodeAction? Fix(string id, Document document, SyntaxNode root, SyntaxNode node) => id switch
    {
        "TAC001" => MakePrivate(document, root, node),
        "TAC002" => ReadOnlyView(document, node),
        "TAC005" => PrivateConstructor(document, root, node),
        "TAC010" => RemoveSetter(document, root, node),
        "TAC012" => AddEqualityComponent(document, node),
        "TAC013" => AddValueEquality(document, node),
        "TAC030" => SetToInit(document, root, node),
        _ => null,
    };

    // ------------------------------------------------------------- TAC001: setter or field → private

    private static CodeAction? MakePrivate(Document document, SyntaxNode root, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<AccessorDeclarationSyntax>() is { } accessor &&
            accessor.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration)
        {
            return CodeAction.Create("Make the setter private", _ =>
            {
                var replacement = accessor
                    .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(Space)))
                    .WithKeyword(Token(SyntaxKind.SetKeyword).WithTriviaFrom(accessor.Keyword))
                    .WithLeadingTrivia(accessor.GetLeadingTrivia());
                var changed = accessor.IsKind(SyntaxKind.InitAccessorDeclaration)
                    ? replacement.WithKind(SyntaxKind.SetAccessorDeclaration)
                    : replacement;
                return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(accessor, changed)));
            }, "TAC001.setter");
        }

        if (node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { Declaration.Variables.Count: 1 } field)
        {
            return CodeAction.Create("Make the field private", _ =>
                Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(field, WithAccessibility(field, SyntaxKind.PrivateKeyword)))),
                "TAC001.field");
        }

        return null;
    }

    // ------------------------------------------------------------- TAC002: List<T> → private field + IReadOnlyList<T>

    private static CodeAction? ReadOnlyView(Document document, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is not { } property) return null;
        if (property.Parent is not TypeDeclarationSyntax type || !IsAutoProperty(property)) return null;
        if (property.Modifiers.Any(SyntaxKind.StaticKeyword) || ReadOnlyType(property.Type) is not { } view) return null;

        var fieldName = "_" + char.ToLowerInvariant(property.Identifier.ValueText[0]) + property.Identifier.ValueText.Substring(1);
        if (type.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).Any(v => v.Identifier.ValueText == fieldName))
            return null;

        return CodeAction.Create($"Expose {property.Identifier.ValueText} as {view} over a private field", async cancellationToken =>
        {
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var symbol = model?.GetDeclaredSymbol(property, cancellationToken);
            if (model is null || symbol is null) return document;

            // Inside the type, every use of the property becomes the field: Lines.Add(...) → _lines.Add(...).
            var uses = type.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(n => n.Identifier.ValueText == property.Identifier.ValueText &&
                            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, cancellationToken).Symbol, symbol))
                .ToList();

            var initializer = property.Initializer ??
                (uses.Count == 0 ? EqualsValueClause(ImplicitObjectCreationExpression()) : null);
            var assignedOutsideConstructors = false;
            var field = FieldDeclaration(
                    VariableDeclaration(property.Type.WithoutTrivia())
                        .AddVariables(VariableDeclarator(fieldName).WithInitializer(initializer)))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)));

            foreach (var use in uses)
            {
                if (use.Parent is AssignmentExpressionSyntax assignment && assignment.Left == use &&
                    use.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is null)
                    assignedOutsideConstructors = true;
            }

            if (assignedOutsideConstructors) field = field.WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)));

            // The field goes on its own line right above the property, with the property's indentation.
            var indentation = property.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).LastOrDefault();
            field = field.NormalizeWhitespace()
                .WithLeadingTrivia(property.GetLeadingTrivia())
                .WithTrailingTrivia(property.GetTrailingTrivia());

            var view2 = PropertyDeclaration(ParseTypeName(view), property.Identifier.WithoutTrivia())
                .WithModifiers(TokenList(property.Modifiers.Select(m => m.WithoutTrivia())))
                .WithExpressionBody(ArrowExpressionClause(IdentifierName(fieldName)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace()
                .WithLeadingTrivia(indentation == default ? property.GetLeadingTrivia() : TriviaList(indentation))
                .WithTrailingTrivia(property.GetTrailingTrivia());

            var tracked = type.TrackNodes(uses.Cast<SyntaxNode>().Append(property));
            foreach (var use in uses)
            {
                var current = tracked.GetCurrentNode(use)!;
                tracked = tracked.ReplaceNode(current, IdentifierName(fieldName).WithTriviaFrom(current));
            }

            var currentProperty = tracked.GetCurrentNode(property)!;
            tracked = tracked.InsertNodesBefore(currentProperty, [field]);
            tracked = tracked.ReplaceNode(tracked.GetCurrentNode(property)!, view2);

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            return document.WithSyntaxRoot(root!.ReplaceNode(type, tracked));
        }, "TAC002.view");
    }

    /// <summary>The read-only view of a mutable collection type, as written in the source.</summary>
    private static string? ReadOnlyType(TypeSyntax type)
    {
        if (type is ArrayTypeSyntax array) return $"IReadOnlyList<{array.ElementType}>";
        var name = type is QualifiedNameSyntax qualified ? qualified.Right : type as SimpleNameSyntax;
        if (name is not GenericNameSyntax generic) return null;
        var arguments = string.Join(", ", generic.TypeArgumentList.Arguments.Select(a => a.ToString()));
        return generic.Identifier.ValueText switch
        {
            "List" or "IList" or "Collection" or "ObservableCollection" => $"IReadOnlyList<{arguments}>",
            "ICollection" or "HashSet" or "ISet" or "SortedSet" or "LinkedList" or "Queue" or "Stack" => $"IReadOnlyCollection<{arguments}>",
            "Dictionary" or "IDictionary" or "SortedDictionary" or "SortedList" => $"IReadOnlyDictionary<{arguments}>",
            _ => null,
        };
    }

    // ------------------------------------------------------------- TAC005: public parameterless constructor → private

    private static CodeAction? PrivateConstructor(Document document, SyntaxNode root, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is { } constructor)
        {
            return CodeAction.Create("Make the constructor private", _ =>
                Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(constructor, WithAccessibility(constructor, SyntaxKind.PrivateKeyword)))),
                "TAC005.private");
        }

        if (node.FirstAncestorOrSelf<ClassDeclarationSyntax>() is not { } type) return null;
        return CodeAction.Create($"Add a private constructor for the ORM", _ =>
        {
            var constructor = ConstructorDeclaration(type.Identifier.WithoutTrivia())
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)))
                .WithBody(Block())
                .WithAdditionalAnnotations(Formatter.Annotation);

            // After the fields and properties, before the first constructor or method.
            var anchor = type.Members.FirstOrDefault(m => m is not (FieldDeclarationSyntax or PropertyDeclarationSyntax or EventFieldDeclarationSyntax));
            var changed = anchor is null ? type.AddMembers(constructor) : type.InsertNodesBefore(anchor, [constructor]);
            return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(type, changed)));
        }, "TAC005.add");
    }

    // ------------------------------------------------------------- TAC010: value object setter → get-only

    private static CodeAction? RemoveSetter(Document document, SyntaxNode root, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<AccessorDeclarationSyntax>() is { Parent: AccessorListSyntax list } accessor &&
            accessor.IsKind(SyntaxKind.SetAccessorDeclaration) && accessor.Body is null && accessor.ExpressionBody is null)
        {
            return CodeAction.Create("Remove the setter (assign it in the constructor)", _ =>
                Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(list, list.RemoveNode(accessor, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeAccessors()))),
                "TAC010.setter");
        }

        if (node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } field)
        {
            return CodeAction.Create("Make the field readonly", _ =>
                Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(field, AddReadOnly(field)))),
                "TAC010.field");
        }

        return null;
    }

    // ------------------------------------------------------------- TAC030: event setter → init

    private static CodeAction? SetToInit(Document document, SyntaxNode root, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<AccessorDeclarationSyntax>() is { } accessor && accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
        {
            return CodeAction.Create("Use init instead of set", _ =>
            {
                var changed = AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithAttributeLists(accessor.AttributeLists)
                    .WithModifiers(accessor.Modifiers)
                    .WithKeyword(Token(SyntaxKind.InitKeyword).WithTriviaFrom(accessor.Keyword))
                    .WithBody(accessor.Body)
                    .WithExpressionBody(accessor.ExpressionBody)
                    .WithSemicolonToken(accessor.SemicolonToken)
                    .WithTriviaFrom(accessor);
                return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(accessor, changed)));
            }, "TAC030.init");
        }

        if (node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } field)
        {
            return CodeAction.Create("Make the field readonly", _ =>
                Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(field, AddReadOnly(field)))),
                "TAC030.field");
        }

        return null;
    }

    // ------------------------------------------------------------- TAC012: add the member to the equality components

    private static CodeAction? AddEqualityComponent(Document document, SyntaxNode node)
    {
        var name = node switch
        {
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
            _ => node.FirstAncestorOrSelf<VariableDeclaratorSyntax>()?.Identifier.ValueText ??
                 node.FirstAncestorOrSelf<PropertyDeclarationSyntax>()?.Identifier.ValueText,
        };

        if (name is null || node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } type) return null;
        var method = type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m =>
            m.Identifier.ValueText is "GetEqualityComponents" or "GetAtomicValues" or "EqualityComponents");

        // Only an iterator body ("yield return A; yield return B;") can take one more line safely.
        if (method?.Body is not { } body || !body.DescendantNodes().OfType<YieldStatementSyntax>().Any()) return null;

        return CodeAction.Create($"Add {name} to {method.Identifier.ValueText}()", async cancellationToken =>
        {
            var statement = YieldStatement(SyntaxKind.YieldReturnStatement, IdentifierName(name))
                .WithAdditionalAnnotations(Formatter.Annotation);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var last = body.Statements.LastOrDefault(s => s is YieldStatementSyntax);
            var changed = last is null ? body.AddStatements(statement) : body.InsertNodesAfter(last, [statement]);
            return document.WithSyntaxRoot(root!.ReplaceNode(body, changed));
        }, "TAC012.add");
    }

    // ------------------------------------------------------------- TAC013: generate Equals and GetHashCode

    private static CodeAction? AddValueEquality(Document document, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<ClassDeclarationSyntax>() is not { } type) return null;

        return CodeAction.Create("Add value equality (Equals and GetHashCode)", async cancellationToken =>
        {
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model?.GetDeclaredSymbol(type, cancellationToken) is not { } symbol) return document;

            var members = State(symbol).Select(m => m.Name).ToList();
            var nullable = model.GetNullableContext(type.SpanStart).AnnotationsEnabled();
            var objectType = nullable ? "object?" : "object";
            var name = type.Identifier.ValueText;

            var comparison = members.Count == 0
                ? $"obj is {name}"
                : $"obj is {name} other && " + string.Join(" && ", members.Select(m => $"Equals({m}, other.{m})"));
            var hash = members.Count switch
            {
                0 => "0",
                <= 8 => $"System.HashCode.Combine({string.Join(", ", members)})",
                _ => members.Skip(8).Aggregate($"System.HashCode.Combine({string.Join(", ", members.Take(8))})",
                    (h, m) => $"System.HashCode.Combine({h}, {m})"),
            };

            var hasHashCode = model.Compilation.GetTypeByMetadataName("System.HashCode") is not null;
            if (!hasHashCode)
            {
                hash = members.Count == 0
                    ? "0"
                    : members.Aggregate("17", (h, m) => $"unchecked(({h}) * 31 + ({m}?.GetHashCode() ?? 0))");
            }

            var equals = (MemberDeclarationSyntax)ParseMemberDeclaration(
                $"public override bool Equals({objectType} obj) => {comparison};\n")!;
            var hashCode = (MemberDeclarationSyntax)ParseMemberDeclaration(
                $"public override int GetHashCode() => {hash};\n")!;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var changed = type.AddMembers(
                equals.WithAdditionalAnnotations(Formatter.Annotation),
                hashCode.WithAdditionalAnnotations(Formatter.Annotation));
            return document.WithSyntaxRoot(root!.ReplaceNode(type, changed));
        }, "TAC013.equality");
    }

    // ------------------------------------------------------------- helpers

    private static IEnumerable<ISymbol> State(INamedTypeSymbol type)
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

    private static bool IsAutoProperty(PropertyDeclarationSyntax property) =>
        property.ExpressionBody is null &&
        property.AccessorList is { } list &&
        list.Accessors.All(a => a.Body is null && a.ExpressionBody is null);

    private static readonly SyntaxKind[] AccessibilityKinds =
        [SyntaxKind.PublicKeyword, SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword];

    private static TMember WithAccessibility<TMember>(TMember member, SyntaxKind accessibility) where TMember : MemberDeclarationSyntax
    {
        var modifiers = member.Modifiers;
        var first = modifiers.FirstOrDefault(m => AccessibilityKinds.Contains(m.Kind()));
        var rest = TokenList(modifiers.Where(m => !AccessibilityKinds.Contains(m.Kind())));
        var token = Token(accessibility);
        token = first == default
            ? token.WithTrailingTrivia(Space)
            : token.WithTriviaFrom(first);
        if (first == default)
        {
            // No modifier yet: the new one takes the member's indentation.
            token = token.WithLeadingTrivia(member.GetLeadingTrivia());
            return (TMember)member.WithoutLeadingTrivia().WithModifiers(rest.Insert(0, token));
        }

        return (TMember)member.WithModifiers(rest.Insert(0, token));
    }

    private static FieldDeclarationSyntax AddReadOnly(FieldDeclarationSyntax field)
    {
        if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return field;
        var modifiers = field.Modifiers;
        var index = modifiers.IndexOf(modifiers.LastOrDefault(m => AccessibilityKinds.Contains(m.Kind()) || m.IsKind(SyntaxKind.NewKeyword))) + 1;
        return field.WithModifiers(modifiers.Insert(index, Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(Space)));
    }
}

internal static class SyntaxExtensions
{
    /// <summary><c>{ get; }</c> with the spacing of the original list.</summary>
    public static AccessorListSyntax NormalizeAccessors(this AccessorListSyntax list) =>
        list.WithAccessors(List(list.Accessors.Select((a, i) =>
            i == list.Accessors.Count - 1 ? a.WithTrailingTrivia(Space) : a)));

    public static AccessorDeclarationSyntax WithKind(this AccessorDeclarationSyntax accessor, SyntaxKind kind) =>
        AccessorDeclaration(kind)
            .WithAttributeLists(accessor.AttributeLists)
            .WithModifiers(accessor.Modifiers)
            .WithKeyword(Token(kind == SyntaxKind.SetAccessorDeclaration ? SyntaxKind.SetKeyword : SyntaxKind.InitKeyword).WithTriviaFrom(accessor.Keyword))
            .WithBody(accessor.Body)
            .WithExpressionBody(accessor.ExpressionBody)
            .WithSemicolonToken(accessor.SemicolonToken)
            .WithTriviaFrom(accessor);
}
