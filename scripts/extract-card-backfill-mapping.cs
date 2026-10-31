#!/usr/bin/env dotnet

// Extract the (Transaction.Id, Transaction.AccountId) mapping from a pre-IntroduceAccountAggregate
// backup — used as the backfill source for RECEIPTS-553's Transaction.CardId column.
//
// The backup is a raw PGDATA directory from before migration 20260415114934_IntroduceAccountAggregate
// was applied to prod. In that schema, there is no `Cards` table and no `Card.AccountId` FK — the
// `Accounts` table IS the card, and `Transactions.AccountId` is the card-of-origin.
//
// Usage:
//   dotnet run scripts/extract-card-backfill-mapping.cs -- [<backup-pgdata-dir>]
// Default backup path: C:/Users/mggar/Downloads/receipts_db-data_2026-04-18.tar
//
// Writes:
//   scripts/data/transaction-card-backfill.csv
//   scripts/data/transaction-card-backfill.generated.sql   <- inline VALUES block for the migration
//
// Requires: Docker Desktop running (or any local docker). Image pgvector/pgvector:0.8.7-pg17.

using System.Diagnostics;
using System.Text;

// The default path points at an *extracted* PGDATA directory, not the .tar archive.
// Despite the ".tar" suffix, the on-disk entry is a directory (this is how the user
// extracted the Docker volume export). BUG-003 caught that the suffix can mislead
// anyone downloading a fresh archive — check for that case below and fail helpfully.
string backupPath = args.Length > 0
    ? args[0]
    : "C:/Users/mggar/Downloads/receipts_db-data_2026-04-18.tar";

string repoRoot = GetRepoRoot();
string dataDir = Path.Combine(repoRoot, "scripts", "data");
string csvPath = Path.Combine(dataDir, "transaction-card-backfill.csv");
string sqlPath = Path.Combine(dataDir, "transaction-card-backfill.generated.sql");

if (!Directory.Exists(backupPath))
{
    if (File.Exists(backupPath))
    {
        Console.Error.WriteLine($"Error: backup path is a file, not a PGDATA directory: {backupPath}");
        Console.Error.WriteLine("If this is a tar archive, extract it first, then pass the extracted directory.");
        Console.Error.WriteLine($"  e.g. tar -xf \"{backupPath}\" -C \"$(dirname \"{backupPath}\")\"");
    }
    else
    {
        Console.Error.WriteLine($"Error: backup directory not found: {backupPath}");
    }
    return 1;
}

string pgVersionFile = Path.Combine(backupPath, "PG_VERSION");
if (!File.Exists(pgVersionFile))
{
    Console.Error.WriteLine($"Error: {backupPath} does not look like a PGDATA directory (missing PG_VERSION).");
    return 1;
}

string pgVersion = File.ReadAllText(pgVersionFile).Trim();
if (pgVersion != "17")
{
    Console.Error.WriteLine($"Error: expected PG_VERSION=17, got '{pgVersion}'. Adjust the image tag if intentional.");
    return 1;
}

if (!DockerAvailable())
{
    Console.Error.WriteLine("Error: docker is not available. Start Docker Desktop and retry.");
    return 1;
}

string scratchDir = Path.Combine(Path.GetTempPath(), $"receipts-backup-extract-{Guid.NewGuid():N}");
string containerName = $"receipts-backfill-{Guid.NewGuid():N}"[..24];

Console.WriteLine($"Copying backup → scratch: {scratchDir}");
CopyDirectory(backupPath, scratchDir);

try
{
    // Overwrite pg_hba.conf so we can connect without the prod password.
    // This only affects the scratch copy; the original backup is untouched.
    string hbaPath = Path.Combine(scratchDir, "pg_hba.conf");
    File.WriteAllText(hbaPath, """
        # Extraction-only trust config — DO NOT USE IN PRODUCTION.
        local   all             all                                     trust
        host    all             all             127.0.0.1/32            trust
        host    all             all             ::1/128                 trust
        """);

    // Clear any stale postmaster lock — the prod container may have written one.
    string lockFile = Path.Combine(scratchDir, "postmaster.pid");
    if (File.Exists(lockFile))
    {
        File.Delete(lockFile);
    }

    string mountPath = ToDockerMountPath(scratchDir);

    Console.WriteLine("Starting sidecar Postgres…");
    Run("docker", [
        "run", "--rm", "-d",
        "--name", containerName,
        "-v", $"{mountPath}:/var/lib/postgresql/data",
        "pgvector/pgvector:0.8.7-pg17",
    ], captureStdout: false);

    Console.WriteLine("Waiting for pg_isready…");
    WaitForReady(containerName);

    Console.WriteLine("Extracting Transactions → CSV…");
    string csv = RunDockerExec(containerName, [
        "psql", "-U", "receipts", "-d", "receipts", "-Atc",
        "COPY (SELECT \"Id\"::text, \"AccountId\"::text FROM \"Transactions\" ORDER BY \"Id\") TO STDOUT WITH CSV HEADER"
    ]);

    Directory.CreateDirectory(dataDir);
    File.WriteAllText(csvPath, csv);

    int rowCount = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
    Console.WriteLine($"Wrote {rowCount} rows to {csvPath}");

    // Derive the SQL VALUES block for embedding in the migration.
    // Parse rows first — an empty backup (header only) must produce a no-op SQL file
    // rather than an invalid `FROM (VALUES)` that only fails at migration-time (BUG-002).
    List<(string Id, string CardId)> pairs = [];
    using (StringReader reader = new(csv))
    {
        _ = reader.ReadLine(); // skip header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts = line.Split(',');
            if (parts.Length != 2) continue;
            pairs.Add((parts[0].Trim(), parts[1].Trim()));
        }
    }

    StringBuilder sb = new();
    sb.AppendLine("-- Generated by scripts/extract-card-backfill-mapping.cs");
    sb.AppendLine("-- Maps Transaction.Id -> original Card.Id (pre-merge state from 2026-04-18 prod backup).");
    sb.AppendLine("-- RECEIPTS-553: backfill Transaction.CardId.");
    sb.AppendLine();

    if (pairs.Count == 0)
    {
        sb.AppendLine("-- Backup contained zero Transactions. No backfill needed; this file is a no-op.");
        Console.Error.WriteLine("Warning: backup had zero Transactions rows — generated SQL is a no-op.");
    }
    else
    {
        sb.Append("UPDATE \"Transactions\" t SET \"CardId\" = m.original_card_id FROM (VALUES");
        for (int i = 0; i < pairs.Count; i++)
        {
            (string id, string cardId) = pairs[i];
            sb.AppendLine(i == 0 ? string.Empty : ",");
            sb.Append($"    ('{id}'::uuid, '{cardId}'::uuid)");
        }

        sb.AppendLine();
        sb.AppendLine(") AS m(id, original_card_id)");
        sb.AppendLine("WHERE t.\"Id\" = m.id");
        sb.AppendLine("  AND EXISTS (SELECT 1 FROM \"Cards\" c WHERE c.\"Id\" = m.original_card_id);");
    }

    File.WriteAllText(sqlPath, sb.ToString());
    Console.WriteLine($"Wrote SQL fragment to {sqlPath}");
}
finally
{
    Console.WriteLine("Stopping sidecar…");
    Run("docker", ["rm", "-f", containerName], captureStdout: false, ignoreExit: true);

    Console.WriteLine("Cleaning up scratch…");
    try
    {
        if (Directory.Exists(scratchDir))
        {
            Directory.Delete(scratchDir, recursive: true);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: failed to delete scratch: {ex.Message}");
    }
}

Console.WriteLine("Done.");
return 0;

// ─────────────────────────────────────────────────────────────────────────────

static string GetRepoRoot()
{
    string d = Directory.GetCurrentDirectory();
    while (d is not null && !Directory.Exists(Path.Combine(d, ".git")) && !File.Exists(Path.Combine(d, ".git")))
    {
        d = Path.GetDirectoryName(d)!;
    }
    return d ?? Directory.GetCurrentDirectory();
}

static bool DockerAvailable()
{
    try
    {
        ProcessStartInfo psi = new("docker", ["version", "--format", "{{.Server.Version}}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process? p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(5000);
        return p.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

static void CopyDirectory(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(dest, Path.GetRelativePath(source, dir));
        Directory.CreateDirectory(target);
    }
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(dest, Path.GetRelativePath(source, file));
        File.Copy(file, target, overwrite: true);
    }
}

static string ToDockerMountPath(string path)
{
    // Docker Desktop on Windows accepts forward-slash paths with drive letter (C:/...).
    // The MSYS/bash environment may also pass the path through MSYS2 path conversion;
    // using forward slashes avoids that.
    return path.Replace('\\', '/');
}

static string Run(string file, string[] args, bool captureStdout = true, bool ignoreExit = false)
{
    ProcessStartInfo psi = new(file)
    {
        RedirectStandardOutput = captureStdout,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string a in args) psi.ArgumentList.Add(a);

    using Process? p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");

    // Read stdout and stderr concurrently to avoid the pipe-buffer deadlock:
    // psql's COPY TO STDOUT can emit >64 KB of data, and any stderr byte arriving
    // mid-flight would block the child while we're still waiting on stdout (BUG-001).
    Task<string> stdoutTask = captureStdout
        ? p.StandardOutput.ReadToEndAsync()
        : Task.FromResult(string.Empty);
    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
    Task.WaitAll(stdoutTask, stderrTask);
    p.WaitForExit();

    string stdout = stdoutTask.Result;
    string stderr = stderrTask.Result;

    if (p.ExitCode != 0 && !ignoreExit)
    {
        throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited {p.ExitCode}: {stderr}");
    }

    return stdout;
}

static string RunDockerExec(string container, string[] innerArgs)
{
    string[] full = ["exec", container, .. innerArgs];
    return Run("docker", full);
}

static void WaitForReady(string container)
{
    for (int i = 0; i < 30; i++)
    {
        try
        {
            Run("docker", ["exec", container, "pg_isready", "-U", "receipts", "-d", "receipts"], captureStdout: true);
            return;
        }
        catch
        {
            Thread.Sleep(1000);
        }
    }
    throw new InvalidOperationException("Postgres did not become ready within 30s");
}
