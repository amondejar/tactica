using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Tactica.Tests;

/// <summary>Marks a test: a public static method with no parameters.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what ?? "Value"}: expected <{expected}>, got <{actual}>");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new AssertionException($"Expected text to contain <{expected}>:\n{actual}");
    }
}

/// <summary>
/// Runs the analyzer over C# source with markup: <c>{|TAC001:text|}</c> marks where a diagnostic is expected.
/// Every expected diagnostic must be reported at that span, and nothing else.
/// </summary>
public static partial class Verify
{
    /// <summary>A small shared kernel, like the base classes of a hand-written domain model.</summary>
    public const string Kernel = """
        namespace Kernel
        {
            using System.Collections.Generic;
            using System.Linq;

            public interface IDomainEvent { }

            public abstract class Entity<TId> where TId : notnull
            {
                public TId Id { get; protected set; } = default!;
                protected Entity(TId id) => Id = id;
                protected Entity() { }
                public override bool Equals(object? obj) => obj is Entity<TId> other && other.GetType() == GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);
                public override int GetHashCode() => Id.GetHashCode();
            }

            public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
            {
                private readonly List<IDomainEvent> _events = new();
                public IReadOnlyCollection<IDomainEvent> DomainEvents => _events.AsReadOnly();
                protected AggregateRoot(TId id) : base(id) { }
                protected AggregateRoot() { }
                protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);
                public void ClearDomainEvents() => _events.Clear();
            }

            public abstract class ValueObject
            {
                protected abstract IEnumerable<object?> GetEqualityComponents();
                public override bool Equals(object? obj) => obj is ValueObject other && other.GetType() == GetType() && GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
                public override int GetHashCode() => GetEqualityComponents().Aggregate(0, (h, c) => System.HashCode.Combine(h, c));
            }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
    ];

    [GeneratedRegex(@"\{\|(?<id>TAC\d{3}):(?<text>.*?)\|\}", RegexOptions.Singleline)]
    private static partial Regex Marker();

    /// <summary>Checks the diagnostics of <paramref name="markup"/> (compiled with the kernel) and returns them.</summary>
    public static ImmutableArray<Diagnostic> Diagnostics(
        string markup, IReadOnlyDictionary<string, string>? options = null, string? baseline = null, bool kernel = true)
    {
        var expected = new List<(string Id, TextSpan Span)>();
        var source = new StringBuilder();
        var last = 0;
        foreach (Match match in Marker().Matches(markup))
        {
            source.Append(markup, last, match.Index - last);
            var text = match.Groups["text"].Value;
            expected.Add((match.Groups["id"].Value, new TextSpan(source.Length, text.Length)));
            source.Append(text);
            last = match.Index + match.Length;
        }

        source.Append(markup, last, markup.Length - last);
        var diagnostics = Run(source.ToString(), options, baseline, kernel);

        var actual = diagnostics.Where(d => d.Location.SourceTree?.FilePath == "Test.cs")
            .Select(d => (d.Id, d.Location.SourceSpan)).ToList();
        var missing = expected.Except(actual).ToList();
        var unexpected = actual.Except(expected).ToList();
        if (missing.Count > 0 || unexpected.Count > 0)
        {
            var text = source.ToString();
            string Describe((string Id, TextSpan Span) d) => $"{d.Id} at '{text.Substring(d.Span.Start, d.Span.Length)}' ({d.Span})";
            throw new AssertionException(
                "Diagnostics differ.\n" +
                (missing.Count > 0 ? "  Missing:\n" + string.Join("\n", missing.Select(d => "    " + Describe(d))) + "\n" : "") +
                (unexpected.Count > 0 ? "  Unexpected:\n" + string.Join("\n", unexpected.Select(d =>
                    "    " + Describe(d) + "\n      " + diagnostics.First(x => x.Id == d.Id && x.Location.SourceSpan == d.Item2).GetMessage())) + "\n" : ""));
        }

        return diagnostics;
    }

    /// <summary>The analyzer's diagnostics, after checking that the source compiles.</summary>
    public static ImmutableArray<Diagnostic> Run(string source, IReadOnlyDictionary<string, string>? options = null, string? baseline = null, bool kernel = true)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, parse, "Test.cs") };
        if (kernel) trees.Add(CSharpSyntaxTree.ParseText(Kernel, parse, "Kernel.cs"));

        var compilation = CSharpCompilation.Create("Test", trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new AssertionException("The test source does not compile:\n" + string.Join("\n", errors));

        var additional = baseline is null ? ImmutableArray<AdditionalText>.Empty : [new Text("/repo/tactica.baseline", baseline)];
        var analyzerOptions = new AnalyzerOptions(additional, new OptionsProvider(options ?? new Dictionary<string, string>()));
        var exceptions = new List<Exception>();
        var analyzers = compilation.WithAnalyzers([new TacticaAnalyzer()], new CompilationWithAnalyzersOptions(
            analyzerOptions, (e, _, _) => exceptions.Add(e), concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        var result = analyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        if (exceptions.Count > 0) throw new AssertionException("The analyzer threw: " + exceptions[0]);
        return [.. result.OrderBy(d => d.Location.SourceSpan.Start)];
    }

    private sealed class Text(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }

    private sealed class OptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private readonly Options _global = new(values);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
    }

    private sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static readonly Options Empty = new(new Dictionary<string, string>());
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => (Name: $"{m.DeclaringType!.Name}.{m.Name}", Method: m))
            .Where(t => args.Length == 0 || args.Any(a => t.Name.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var failed = 0;
        foreach (var (name, method) in tests)
        {
            try
            {
                method.Invoke(null, null);
                Console.WriteLine($"  ok    {name}");
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}\n{Indent(e.InnerException is AssertionException ? e.InnerException.Message : e.InnerException.ToString())}");
            }
        }

        Console.WriteLine($"\n{tests.Count - failed} passed, {failed} failed, {tests.Count} total");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }

    private static string Indent(string text) => string.Join("\n", text.Split('\n').Select(l => "        " + l));
}
