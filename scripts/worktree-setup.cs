#!/usr/bin/env dotnet

using System.Diagnostics;

string repoRoot = GetRepoRoot();
bool checkOnly = args.Length > 0 && args[0] == "--check";

if (checkOnly)
{
    List<string> missing = [];

    if (!Directory.Exists(Path.Combine(repoRoot, "node_modules")))
    {
        missing.Add("node_modules/ — run: npm install");
    }

    if (!Directory.Exists(Path.Combine(repoRoot, "src", "client", "node_modules")))
    {
        missing.Add("src/client/node_modules/ — run: cd src/client && npm install");
    }

    bool hasObj = Directory.GetDirectories(Path.Combine(repoRoot, "src"), "obj", SearchOption.AllDirectories).Length > 0;
    if (!hasObj)
    {
        missing.Add("src/*/obj/ — run: dotnet restore Receipts.slnx");
    }

    if (!File.Exists(Path.Combine(repoRoot, "openapi", "generated", "API.json")))
    {
        missing.Add("openapi/generated/API.json — run: dotnet build Receipts.slnx");
    }

    string generatedDir = Path.Combine(repoRoot, "src", "Presentation", "API", "Generated");
    bool hasGeneratedCs = Directory.Exists(generatedDir) && Directory.GetFiles(generatedDir, "*.g.cs").Length > 0;
    if (!hasGeneratedCs)
    {
        missing.Add("src/Presentation/API/Generated/*.g.cs — run: dotnet build Receipts.slnx");
    }

    if (!File.Exists(Path.Combine(repoRoot, "src", "client", "src", "generated", "api.d.ts")))
    {
        missing.Add("src/client/src/generated/api.d.ts — run: cd src/client && npm run generate:types:write");
    }

    // The model lives in a per-machine cache rather than in the repo (RECEIPTS-929), so it
    // is downloaded once and shared by every clone and worktree.
    if (!File.Exists(Path.Combine(ResolveModelDirectory(), "model.onnx")))
    {
        missing.Add("ONNX model — run: dotnet run scripts/download-onnx-model.cs");
    }

    if (missing.Count > 0)
    {
        Console.Error.WriteLine("Pre-commit prerequisites missing:");
        foreach (string item in missing)
        {
            Console.Error.WriteLine($"  - {item}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Run 'dotnet run scripts/worktree-setup.cs' to fix all at once.");
        return 1;
    }

    Console.WriteLine("All prerequisites present.");
    return 0;
}

Console.WriteLine("==> Downloading ONNX model if needed...");
await RunAsync("dotnet", ["run", Path.Combine(repoRoot, "scripts", "download-onnx-model.cs")], repoRoot);

Console.WriteLine("==> Restoring NuGet packages and configuring git hooks...");
await RunAsync("dotnet", ["restore", "Receipts.slnx"], repoRoot);

Console.WriteLine("==> Installing root npm dependencies...");
await RunAsync("npm", ["install"], repoRoot);

Console.WriteLine("==> Installing client npm dependencies...");
await RunAsync("npm", ["install"], Path.Combine(repoRoot, "src", "client"));

Console.WriteLine("==> Building solution (generates DTOs and openapi/generated/API.json)...");
await RunAsync("dotnet", ["build", "Receipts.slnx"], repoRoot);

Console.WriteLine("==> Generating TypeScript types from OpenAPI spec...");
await RunAsync("npm", ["run", "generate:types:write"], Path.Combine(repoRoot, "src", "client"));

Console.WriteLine("==> Worktree setup complete.");
return 0;

static async Task<int> RunAsync(string command, string[] arguments, string workingDirectory)
{
    // On Windows, .cmd/.bat scripts (like npm) require shell execution
    // since Process.Start with UseShellExecute=false can't resolve them.
    bool isWindows = OperatingSystem.IsWindows();
    string resolvedCommand = isWindows && !Path.HasExtension(command) && command != "dotnet" && command != "git"
        ? "cmd"
        : command;
    string[] resolvedArgs = resolvedCommand == "cmd"
        ? ["/c", command, .. arguments]
        : arguments;

    ProcessStartInfo psi = new(resolvedCommand, resolvedArgs)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
    };

    using Process? process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine($"Error: Failed to start {command}");
        return 1;
    }

    await process.WaitForExitAsync();
    return process.ExitCode;
}

static string GetRepoRoot()
{
    ProcessStartInfo psi = new("git", ["rev-parse", "--show-toplevel"])
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };

    using Process? proc = Process.Start(psi);
    string output = proc!.StandardOutput.ReadToEnd().Trim();
    proc.WaitForExit();
    return output;
}

// Mirrors EmbeddingModelOptions.ResolveModelDirectory and download-onnx-model.cs.
static string ResolveModelDirectory()
{
    if (Environment.GetEnvironmentVariable("Embeddings__ModelPath") is { Length: > 0 } configured)
    {
        return configured;
    }

    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    if (string.IsNullOrWhiteSpace(root))
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        root = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "Receipts")
            : Path.Combine(home, ".local", "share");
    }

    return Path.Combine(root, "Receipts", "models", "BgeLargeEnV15");
}
