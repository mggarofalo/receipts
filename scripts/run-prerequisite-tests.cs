#!/usr/bin/env dotnet

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

if (args.Length != 1)
{
	PrintUsage();
	return 2;
}

string repoRoot = GetRepoRoot();
string lane = args[0].ToLowerInvariant();
string project;
string prerequisite;

switch (lane)
{
	case "postgres":
		if (!await DockerIsAvailableAsync(repoRoot))
		{
			Console.Error.WriteLine(
				"PostgreSQL prerequisite unavailable: Docker is not installed, is not running, or cannot be reached. " +
				"Start Docker and rerun this command.");
			return 2;
		}

		project = Path.Combine(repoRoot, "tests", "Infrastructure.IntegrationTests", "Infrastructure.IntegrationTests.csproj");
		prerequisite = "Postgres";
		break;

	case "model":
		Console.WriteLine("Provisioning and verifying the pinned ONNX model...");
		int provisionExitCode = await RunAsync(
			"dotnet",
			[
				"run",
				Path.Combine(repoRoot, "scripts", "download-onnx-model.cs"),
				"--",
				"--verify-existing",
			],
			repoRoot);
		if (provisionExitCode != 0)
		{
			Console.Error.WriteLine(
				"Model prerequisite unavailable: the pinned ONNX model could not be provisioned or verified.");
			return 2;
		}

		project = Path.Combine(repoRoot, "tests", "Infrastructure.Tests", "Infrastructure.Tests.csproj");
		prerequisite = "Model";
		break;

	default:
		Console.Error.WriteLine($"Unknown prerequisite lane '{args[0]}'.");
		PrintUsage();
		return 2;
}

Console.WriteLine($"Prerequisite '{prerequisite}' is ready. Running its tests...");
string resultsDirectory = Path.Combine(
	Path.GetTempPath(),
	"receipts-prerequisite-tests",
	Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(resultsDirectory);

try
{
	int testExitCode = await RunAsync(
		"dotnet",
		[
			"test",
			project,
			"--filter",
			$"Prerequisite={prerequisite}",
			"--logger",
			"console;verbosity=normal",
			"--logger",
			"trx;LogFileName=results.trx",
			"--results-directory",
			resultsDirectory,
		],
		repoRoot);
	if (testExitCode != 0)
	{
		return testExitCode;
	}

	return ValidateExecutedTests(Path.Combine(resultsDirectory, "results.trx"), prerequisite);
}
finally
{
	Directory.Delete(resultsDirectory, recursive: true);
}

static int ValidateExecutedTests(string resultsPath, string prerequisite)
{
	if (!File.Exists(resultsPath))
	{
		Console.Error.WriteLine($"{prerequisite} lane did not produce a test-results file.");
		return 1;
	}

	XElement? counters = XDocument.Load(resultsPath)
		.Descendants()
		.SingleOrDefault(element => element.Name.LocalName == "Counters");
	if (counters is null)
	{
		Console.Error.WriteLine($"{prerequisite} lane produced test results without execution counters.");
		return 1;
	}

	int total = ReadCounter(counters, "total");
	int executed = ReadCounter(counters, "executed");
	int passed = ReadCounter(counters, "passed");
	int failed = ReadCounter(counters, "failed");
	int notExecuted = ReadCounter(counters, "notExecuted");

	Console.WriteLine(
		$"{prerequisite} lane result: {executed} executed, {passed} passed, " +
		$"{failed} failed, {notExecuted} not executed.");

	if (executed == 0)
	{
		Console.Error.WriteLine(
			$"{prerequisite} lane discovered no executable tests; refusing to report success.");
		return 1;
	}

	if (total != executed || notExecuted != 0)
	{
		Console.Error.WriteLine(
			$"{prerequisite} lane did not execute every discovered test; refusing to report success.");
		return 1;
	}

	return 0;
}

static int ReadCounter(XElement counters, string name)
{
	string? value = counters.Attribute(name)?.Value;
	return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
		? result
		: 0;
}

static async Task<bool> DockerIsAvailableAsync(string workingDirectory)
{
	try
	{
		return await RunAsync(
			"docker",
			["info", "--format", "Docker server {{.ServerVersion}} is ready."],
			workingDirectory) == 0;
	}
	catch (Win32Exception)
	{
		return false;
	}
}

static async Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
{
	ProcessStartInfo startInfo = new(fileName, arguments)
	{
		WorkingDirectory = workingDirectory,
		UseShellExecute = false,
	};

	using Process process = Process.Start(startInfo)
		?? throw new InvalidOperationException($"Failed to start {fileName}.");
	await process.WaitForExitAsync();
	return process.ExitCode;
}

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

static void PrintUsage()
{
	Console.Error.WriteLine("Usage: dotnet run scripts/run-prerequisite-tests.cs -- <postgres|model>");
}
