using System.Diagnostics;
using System.Text;
using System.Text.Json;

// tactica: the command line of the Tactica analyzers.
return args switch
{
    ["baseline", .. var rest] => Baseline(rest),
    [] or ["-h" or "--help" or "help"] => Help(0),
    ["--version"] => Version(),
    _ => Help(2),
};

static int Help(int code)
{
    var text = """
        tactica: tactical DDD rules for .NET (https://github.com/amondejar/tactica)

        Usage:
          dotnet tactica baseline [<solution|project|folder>] [--output <file>]

        baseline   Builds the code, records every Tactica finding in tactica.baseline and accepts them: the build then
                   reports only findings in new or changed code. Commit the file. Run it again after fixing findings
                   to shrink it. The file goes to the repository root (or --output): each project uses the nearest
                   tactica.baseline in its folder or above.

        The rules themselves run in the build, from the Tactica package: dotnet add package Tactica
        """;
    (code == 0 ? Console.Out : Console.Error).WriteLine(text);
    return code;
}

static int Version()
{
    Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
    return 0;
}

static int Baseline(string[] args)
{
    var output = Output(args);
    var target = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || args[i - 1] is not ("-o" or "--output"))).FirstOrDefault() ?? ".";
    var full = Path.GetFullPath(target);
    var root = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
    if (!Directory.Exists(full) && !File.Exists(full))
    {
        Console.Error.WriteLine($"tactica: '{target}' does not exist.");
        return 2;
    }

    var logDir = Directory.CreateTempSubdirectory("tactica-").FullName;

    Console.WriteLine($"Building {Path.GetRelativePath(Environment.CurrentDirectory, full)} to collect the findings...");
    var build = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in new[]
             {
                 "build", full, "--no-incremental", "-nologo", "-v:q", "-clp:NoSummary",
                                  "-p:TacticaBaselineMode=true", $"-p:TacticaBaselineLogDir={logDir}",
                 "-p:TreatWarningsAsErrors=false", "-p:MSBuildTreatWarningsAsErrors=false",
             })
    {
        build.ArgumentList.Add(argument);
    }

    var log = new StringBuilder();
    using (var process = Process.Start(build)!)
    {
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        // Tactica findings configured as errors fail the build too; only other errors stop the baseline.
        var errors = log.ToString().Split('\n').Where(l => l.Contains(": error ", StringComparison.Ordinal) && !l.Contains(": error TAC", StringComparison.Ordinal)).ToList();
        if (process.ExitCode != 0 && (errors.Count > 0 || Directory.GetFiles(logDir, "*.sarif").Length == 0))
        {
            Console.Error.WriteLine(log.ToString().TrimEnd());
            Console.Error.WriteLine("tactica: the build failed, so the baseline was not written. Fix the errors above and run it again.");
            Directory.Delete(logDir, recursive: true);
            return 1;
        }
    }

    var logs = Directory.GetFiles(logDir, "*.sarif");
    if (logs.Length == 0)
    {
        Directory.Delete(logDir, recursive: true);
        Console.Error.WriteLine("tactica: no project here references the Tactica package, so there is nothing to record. Add it first: dotnet add package Tactica");
        return 1;
    }

    var keys = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var path in logs) keys.UnionWith(Keys(path));
    Directory.Delete(logDir, recursive: true);

    var file = output ?? Path.Combine(RepositoryRoot(root) ?? root, "tactica.baseline");
    var lines = new List<string>
    {
        "# Tactica baseline: findings accepted when the rules were adopted. The build does not report them;",
        "# it reports any new one. Fix them over time and run \"dotnet tactica baseline\" again to remove them.",
        "",
    };
    lines.AddRange(keys);
    File.WriteAllText(file, string.Join("\n", lines) + "\n");

    var byRule = keys.GroupBy(k => k.Split(' ')[0]).Select(g => $"{g.Key} {g.Count()}");
    Console.WriteLine(keys.Count == 0
        ? $"No findings: wrote an empty {Path.GetRelativePath(Environment.CurrentDirectory, file)}."
        : $"Accepted {keys.Count} findings in {Path.GetRelativePath(Environment.CurrentDirectory, file)} ({string.Join(", ", byRule)}). Commit it.");
    return 0;
}

// The build uses the nearest tactica.baseline in or above each project: the repository root covers them all.
static string? RepositoryRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
    }

    return null;
}

static string? Output(string[] args)
{
    var index = Array.FindIndex(args, a => a is "-o" or "--output");
    return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
}

static IEnumerable<string> Keys(string log)
{
    using var document = JsonDocument.Parse(File.ReadAllText(log));
    foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
    {
        if (!run.TryGetProperty("results", out var results)) continue;
        foreach (var result in results.EnumerateArray())
        {
            if (result.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("customProperties", out var custom) &&
                custom.TryGetProperty("TacticaKey", out var key) &&
                key.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }
}
