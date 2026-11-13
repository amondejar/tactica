using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tactica;

/// <summary>
/// The findings accepted when Tactica was adopted, from <c>tactica.baseline</c> (written by <c>dotnet tactica baseline</c>).
/// One finding per line: the rule and the symbol, so the baseline survives edits elsewhere in the file.
/// </summary>
internal sealed class Baseline
{
    public const string FileName = "tactica.baseline";
    public const string KeyProperty = "TacticaKey";

    private static readonly Baseline Empty = new(new HashSet<string>(StringComparer.Ordinal));
    private readonly HashSet<string> _keys;

    private Baseline(HashSet<string> keys) => _keys = keys;

    public bool Contains(string key) => _keys.Contains(key);

    public static Baseline Read(ImmutableArray<AdditionalText> files, CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            if (!string.Equals(Path.GetFileName(file.Path), FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.GetText(cancellationToken) is not { } text) continue;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in text.Lines)
            {
                var entry = line.ToString().Trim();
                if (entry.Length > 0 && entry[0] != '#') keys.Add(entry);
            }

            return new Baseline(keys);
        }

        return Empty;
    }
}

/// <summary>Reports a finding unless the baseline accepts it, and tags it with its baseline key.</summary>
internal sealed class Reporter(Baseline baseline, bool baselineMode)
{
    public void Report(Action<Diagnostic> report, DiagnosticDescriptor rule, Location location, string subject, string message)
    {
        var key = rule.Id + " " + subject;
        if (!baselineMode && baseline.Contains(key)) return;
        var properties = ImmutableDictionary<string, string?>.Empty.Add(Baseline.KeyProperty, key);
        report(Diagnostic.Create(rule, location, properties, message));
    }

    /// <summary>The stable name of a symbol in the baseline: <c>Shop.Orders.Order.Lines</c>.</summary>
    public static string Key(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
}
