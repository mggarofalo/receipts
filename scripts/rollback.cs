#!/usr/bin/env dotnet

using System.Diagnostics;

string repoRoot = GetRepoRoot();
LoadEnvFile(Path.Combine(repoRoot, ".env"));

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/rollback.cs -- <image_tag>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Example: dotnet run scripts/rollback.cs -- 0.17.1");
    Console.Error.WriteLine("Example: dotnet run scripts/rollback.cs -- sha-abc1234");
    return 1;
}

string tag = args[0];
string appPort = Environment.GetEnvironmentVariable("APP_PORT") ?? "8080";
string healthUrl = $"http://localhost:{appPort}/api/health";

Console.WriteLine($"Rolling back to tag: {tag}");

Dictionary<string, string> tagEnv = new() { ["IMAGE_TAG"] = tag };

await RunAsync("docker", ["compose", "pull", "app"], repoRoot, tagEnv);
await RunAsync("docker", ["compose", "up", "-d", "app"], repoRoot, tagEnv);

Console.WriteLine("Waiting for health check...");
await Task.Delay(5000);

using HttpClient http = new();
try
{
    string body = await http.GetStringAsync(healthUrl);
    if (body.Contains("\"status\""))
    {
        Console.WriteLine($"Rollback to {tag} complete. Health check passed.");
        return 0;
    }
}
catch
{
    // Health check not ready
}

Console.WriteLine("WARNING: Health check not passing yet. Monitor with: docker compose logs -f app");
return 0;

static async Task<int> RunAsync(string command, string[] arguments, string workingDirectory, Dictionary<string, string>? envVars = null)
{
    ProcessStartInfo psi = new(command, arguments)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
    };

    if (envVars is not null)
    {
        foreach (KeyValuePair<string, string> kvp in envVars)
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }
    }

    using Process? process = Process.Start(psi);
    if (process is null)
    {
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

static void LoadEnvFile(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    foreach (string line in File.ReadLines(path))
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            continue;
        }

        int eq = trimmed.IndexOf('=');
        if (eq <= 0)
        {
            continue;
        }

        string key = trimmed[..eq];
        string value = trimmed[(eq + 1)..].Trim('"').Trim('\'');
        Environment.SetEnvironmentVariable(key, value);
    }
}
