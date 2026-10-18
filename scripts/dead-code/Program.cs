using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "--expect-absent"))
{
	Console.Error.WriteLine("Usage: DeadCodeAudit <solution.slnx> <manifest.json> [--expect-absent]");
	return 2;
}

MSBuildLocator.RegisterDefaults();
string solutionPath = Path.GetFullPath(args[0]);
string root = Path.GetDirectoryName(solutionPath)!;
bool expectAbsent = args.Length == 3 && args[2] == "--expect-absent";
Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
if (manifest.Candidates.Count == 0 || manifest.ProductionProjects.Count == 0)
	throw new InvalidOperationException("An explicit candidate and production-project inventory is required.");
using MSBuildWorkspace workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["TreatWarningsAsErrors"] = "false" });
System.Collections.Concurrent.ConcurrentQueue<string> workspaceFailures = new();
workspace.RegisterWorkspaceFailedHandler(e =>
{
	if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
		workspaceFailures.Enqueue(e.Diagnostic.Message);
});
Solution solution = await workspace.OpenSolutionAsync(solutionPath);
Dictionary<string, Compilation> compilations = [];
foreach (Project project in solution.Projects)
{
	Compilation compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException($"Cannot compile {project.FilePath}");
	Diagnostic[] errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
	if (errors.Length != 0)
		throw new InvalidOperationException($"Incomplete binding in {project.Name}:\n{string.Join("\n", errors.Select(e => e.ToString()))}");
	compilations.Add(Relative(project.FilePath!), compilation);
}
if (workspaceFailures.Count != 0)
	throw new InvalidOperationException(string.Join("\n", workspaceFailures));
foreach (string project in compilations.Keys.Where(p => !p.StartsWith("tests/", StringComparison.Ordinal)))
	if (!manifest.ProductionProjects.Contains(project, StringComparer.Ordinal))
		throw new InvalidOperationException($"Unclassified production project: {project}");
foreach (string project in manifest.ProductionProjects)
	if (!compilations.ContainsKey(project))
		throw new InvalidOperationException($"Required production project missing: {project}");

bool failed = false;
foreach (Candidate candidate in manifest.Candidates)
{
	if (!compilations.TryGetValue(candidate.Project, out Compilation? compilation))
		throw new InvalidOperationException($"Candidate project missing: {candidate.Project}");
	string[] parts = candidate.Symbol.Split("::", StringSplitOptions.None);
	INamedTypeSymbol? type = compilation.GetTypeByMetadataName(parts[0]);
	List<ISymbol> symbols = type is null ? [] : parts.Length == 2
		? type.GetMembers(parts[1]).ToList()
		: [type, .. type.GetMembers().Where(s => !s.IsImplicitlyDeclared && s.DeclaringSyntaxReferences.Length > 0)];
	if (expectAbsent)
	{
		if (symbols.Count > 0) { failed = true; Console.WriteLine($"PRESENT {candidate.Symbol}"); }
		else Console.WriteLine($"ABSENT {candidate.Symbol}");
		continue;
	}
	if (symbols.Count == 0 || symbols.Any(s => !SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, compilation.Assembly)))
		throw new InvalidOperationException($"Candidate missing or not owned by declared project: {candidate.Symbol}");
	SortedSet<string> evidence = new(StringComparer.Ordinal);
	foreach (ISymbol symbol in symbols)
	{
		foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution))
		{
			foreach (ReferenceLocation location in referenced.Locations)
			{
				string path = Relative(location.Document.FilePath!);
				bool generated = location.Document is SourceGeneratedDocument;
				string classification = path.StartsWith("tests/", StringComparison.Ordinal) ? "test"
					: candidate.AllowedClusterFiles.Contains(path, StringComparer.Ordinal) ? "cluster"
					: generated && candidate.AllowGeneratedMediatorDispatch && path.Contains("Mediator.SourceGenerator", StringComparison.Ordinal) ? "generated-dispatch"
					: "LIVE-OR-UNCLASSIFIED";
				if (classification == "LIVE-OR-UNCLASSIFIED") failed = true;
				evidence.Add($"{classification} {path}:{location.Location.GetLineSpan().StartLinePosition.Line + 1} {referenced.Definition.ToDisplayString()}");
			}
		}
	}
	Console.WriteLine($"CANDIDATE {candidate.Symbol}");
	foreach (string line in evidence) Console.WriteLine(line);
}
Console.WriteLine(failed ? "REJECTED: candidate has live, unclassified, or remaining symbols." : "PASS: declared candidate conditions satisfied; runtime-root review remains required.");
return failed ? 1 : 0;

string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
internal sealed record Manifest(List<string> ProductionProjects, List<Candidate> Candidates);
internal sealed record Candidate(string Project, string Symbol, List<string> AllowedClusterFiles, bool AllowGeneratedMediatorDispatch = false);
