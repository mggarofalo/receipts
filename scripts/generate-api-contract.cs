#!/usr/bin/env dotnet

using System.Diagnostics;

string repoRoot = GetRepoRoot();
string apiProjectDirectory = Path.Combine(repoRoot, "src", "Presentation", "API");
string generationCache = Path.Combine(apiProjectDirectory, "obj", "API.OpenApiFiles.cache");

// Microsoft.Extensions.ApiDescription.Server tracks the generated contract through
// this cache rather than through API.json itself. Invalidate it so this command
// repairs a missing or externally modified materialized contract.
File.Delete(generationCache);

ProcessStartInfo startInfo = new(
	"dotnet",
	[
		"build",
		Path.Combine(apiProjectDirectory, "API.csproj"),
		"-p:GenerateApiContract=true",
		"-p:TreatWarningsAsErrors=true",
	])
{
	WorkingDirectory = repoRoot,
	UseShellExecute = false,
};
startInfo.Environment["Jwt__Key"] = "build-time-spec-extraction-only-not-for-runtime";
startInfo.Environment["RECEIPTS_CONTRACT_GENERATION"] = "1";

using Process? process = Process.Start(startInfo);
if (process is null)
{
	Console.Error.WriteLine("Failed to start API contract generation.");
	return 1;
}

await process.WaitForExitAsync();
return process.ExitCode;

static string GetRepoRoot()
{
	ProcessStartInfo startInfo = new("git", ["rev-parse", "--show-toplevel"])
	{
		RedirectStandardOutput = true,
		UseShellExecute = false,
	};

	using Process process = Process.Start(startInfo)
		?? throw new InvalidOperationException("Failed to locate the repository root.");
	string output = process.StandardOutput.ReadToEnd().Trim();
	process.WaitForExit();
	return output;
}
