using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Tactica.CodeFixes;

namespace Tactica.Tests;

public static partial class Verify
{
    private static readonly Lazy<MefHostServices> Host = new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));

    /// <summary>
    /// Applies Tactica's code fixes to <paramref name="source"/>, one diagnostic at a time, until none can be fixed, and
    /// checks that the result is <paramref name="expected"/> and compiles.
    /// </summary>
    public static void Fix(string source, string expected, IReadOnlyDictionary<string, string>? options = null, bool kernel = true, bool compiles = true)
    {
        var actual = Fixed(source, options, kernel, compiles);
        if (Normalize(actual) != Normalize(expected))
            throw new AssertionException($"The fixed code differs.\n--- expected\n{expected}\n--- actual\n{actual}");
    }

    public static string Fixed(string source, IReadOnlyDictionary<string, string>? options = null, bool kernel = true, bool compiles = true)
    {
        using var workspace = new AdhocWorkspace(Host.Value);
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Test", "Test", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: parse,
            metadataReferences: References));
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
        if (kernel) workspace.AddDocument(project.Id, "Kernel.cs", SourceText.From(Kernel));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        var provider = new TacticaCodeFixProvider();
        var analyzerOptions = new AnalyzerOptions([], new OptionsProvider(options ?? new Dictionary<string, string>()));
        var unfixable = new HashSet<string>();

        for (var round = 0; round < 50; round++)
        {
            var compilation = document.Project.GetCompilationAsync().GetAwaiter().GetResult()!;
            var diagnostics = compilation.WithAnalyzers([new TacticaAnalyzer()], analyzerOptions)
                .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
                .Where(d => d.Location.SourceTree?.FilePath == "Test.cs" && provider.FixableDiagnosticIds.Contains(d.Id))
                .Where(d => !unfixable.Contains(d.Id + d.Properties["TacticaKey"]))
                .OrderBy(d => d.Location.SourceSpan.Start)
                .ToList();
            if (diagnostics.Count == 0) break;

            var diagnostic = diagnostics[0];
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
            provider.RegisterCodeFixesAsync(context).GetAwaiter().GetResult();
            if (actions.Count == 0)
            {
                unfixable.Add(diagnostic.Id + diagnostic.Properties["TacticaKey"]);
                continue;
            }

            var operations = actions[0].GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult();
            var solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            document = solution.GetDocument(document.Id)!;
        }

        var text = document.GetTextAsync().GetAwaiter().GetResult().ToString();
        if (compiles)
        {
            var errors = document.Project.GetCompilationAsync().GetAwaiter().GetResult()!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0) throw new AssertionException("The fixed code does not compile:\n" + string.Join("\n", errors) + "\n" + text);
        }

        return text;
    }

    /// <summary>
    /// "Fix all" over one document, as dotnet format calls it: every finding of <paramref name="ids"/> at once, under
    /// the equivalence key of the first one.
    /// </summary>
    public static void FixAll(string source, string expected, params string[] ids)
    {
        using var workspace = new AdhocWorkspace(Host.Value);
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Test", "Test", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: References));
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
        workspace.AddDocument(project.Id, "Kernel.cs", SourceText.From(Kernel));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        var compilation = document.Project.GetCompilationAsync().GetAwaiter().GetResult()!;
        var diagnostics = compilation.WithAnalyzers([new TacticaAnalyzer()])
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .Where(d => d.Location.SourceTree?.FilePath == "Test.cs" && ids.Contains(d.Id))
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToImmutableArray();

        var provider = new TacticaCodeFixProvider();
        var first = new List<CodeAction>();
        provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostics[0], (a, _) => first.Add(a), CancellationToken.None)).GetAwaiter().GetResult();

        var context = new FixAllContext(document, provider, FixAllScope.Document, first[0].EquivalenceKey, ids,
            new FixedDiagnostics(diagnostics), CancellationToken.None);
        var action = provider.GetFixAllProvider().GetFixAsync(context).GetAwaiter().GetResult()
            ?? throw new AssertionException("No fix-all action");
        var solution = action.GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult().OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var text = solution.GetDocument(document.Id)!.GetTextAsync().GetAwaiter().GetResult().ToString();
        if (Normalize(text) != Normalize(expected))
            throw new AssertionException($"The fixed code differs.\n--- expected\n{expected}\n--- actual\n{text}");
    }

    private sealed class FixedDiagnostics(ImmutableArray<Diagnostic> all) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Diagnostic>>(all);
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Diagnostic>>(all);
        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Diagnostic>>([]);
    }

    /// <summary>Compiles <paramref name="source"/> and loads it, to check what generated code does.</summary>
    public static System.Reflection.Assembly Emit(string source)
    {
        var compilation = CSharpCompilation.Create("Emitted" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) throw new AssertionException("Does not compile:\n" + string.Join("\n", result.Diagnostics));
        return System.Reflection.Assembly.Load(stream.ToArray());
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
