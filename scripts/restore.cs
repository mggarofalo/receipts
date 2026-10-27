#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

string root = GetRoot();
LoadEnv(Path.Combine(root, ".env"));
// Deterministic SQL output for the disposable-PostgreSQL restore proof.
if ((args.Length == 2 || (args.Length == 3 && args[2] == "--unknown")) && args[0] == "--test-fence-sql")
{
    string testLedger = await File.ReadAllTextAsync(args[1]);
    Validate(testLedger);
    Console.Write(FenceSql(testLedger, args.Length == 3));
    return 0;
}
bool reconcile = args.Length > 0 && args[0] == "--reconcile";
if ((!reconcile && args.Length != 1) || (reconcile && args.Length > 2))
{
    Console.Error.WriteLine("Usage: dotnet run scripts/restore.cs -- <backup.sql.gz> | --reconcile [saved-ledger.csv]");
    return 1;
}
if ((!reconcile && !File.Exists(args[0])) || (reconcile && args.Length == 2 && !File.Exists(args[1])))
{
    Console.Error.WriteLine("Backup or ledger file not found.");
    return 1;
}
Console.WriteLine(reconcile ? "Reconcile restored assistance state while the app is stopped." : $"WARNING: Replace the current database from {args[0]}.");
Console.Write("Continue? [y/N] ");
if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) return 0;

string user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "receipts";
string db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "receipts";
string docker = Environment.GetEnvironmentVariable("RECEIPTS_RESTORE_DOCKER") ?? "docker";
string[] Psql() => ["compose", "exec", "-T", "db", "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", user, "-d", db, "--quiet"];
if (await Run(docker, ["compose", "stop", "app"], root) != 0)
{
    Console.Error.WriteLine("App stop failed. Database was not changed.");
    return 1;
}

string? ledgerPath = reconcile ? args.ElementAtOrDefault(1) : null;
bool stopConfirmed = true;
try
{
    if (!reconcile)
    {
        ledgerPath = Path.GetFullPath(args[0]) + $".assistance-ledger-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.csv";
        string present = await ReadSql(docker, "SELECT to_regclass('category_assistance.\"DailyUsage\"') IS NOT NULL;", Psql(), root)
            ?? throw new InvalidOperationException("Could not inspect the current usage ledger.");
        string ledger = present.Trim() == "t"
            ? await ReadSql(docker, "COPY (SELECT \"UtcDay\", \"DispatchAttempts\" FROM category_assistance.\"DailyUsage\" ORDER BY \"UtcDay\") TO STDOUT WITH (FORMAT csv);", Psql(), root)
                ?? throw new InvalidOperationException("Could not capture the current usage ledger.")
            : "";
        Validate(ledger);
        await using (var ledgerFile = new FileStream(ledgerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await ledgerFile.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ledger));
            ledgerFile.Flush(flushToDisk: true);
        }
        Console.WriteLine($"Saved usage ledger: {ledgerPath}");
        using var restore = Process.Start(new ProcessStartInfo(docker, Psql())
            { WorkingDirectory = root, RedirectStandardInput = true, UseShellExecute = false }) ?? throw new InvalidOperationException("Could not start psql.");
        try
        {
            await restore.StandardInput.WriteLineAsync("BEGIN;");
            await restore.StandardInput.FlushAsync();
            await using var compressed = File.OpenRead(args[0]);
            await using var unzip = new GZipStream(compressed, CompressionMode.Decompress);
            byte[] buffer = new byte[81920];
            long bytes = 0;
            int count;
            while ((count = await unzip.ReadAsync(buffer)) != 0)
            {
                bytes += count;
                await restore.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count));
            }
            if (bytes == 0) throw new InvalidOperationException("Backup decompressed to an empty SQL stream.");
            await restore.StandardInput.WriteLineAsync();
            await restore.StandardInput.WriteLineAsync("COMMIT;");
        }
        finally
        {
            restore.StandardInput.Close();
            await restore.WaitForExitAsync();
        }
        if (restore.ExitCode != 0)
            throw new InvalidOperationException($"Restore failed (psql={restore.ExitCode}).");
    }

    string captured = ledgerPath is null ? "" : await File.ReadAllTextAsync(ledgerPath);
    Validate(captured);
    string schema = await ReadSql(docker, "SELECT to_regclass('category_assistance.\"Settings\"') IS NOT NULL AND to_regclass('category_assistance.\"Connection\"') IS NOT NULL AND to_regclass('category_assistance.\"Attempts\"') IS NOT NULL AND to_regclass('category_assistance.\"DailyUsage\"') IS NOT NULL AND to_regclass('category_assistance.\"SettingsAudit\"') IS NOT NULL AND to_regclass('taxonomy.\"Head\"') IS NOT NULL;", Psql(), root)
        ?? throw new InvalidOperationException("Could not inspect restored assistance schema.");
    if (schema.Trim() != "t")
        throw new InvalidOperationException("Assistance schema is absent. Apply migrations, then run --reconcile with the saved ledger before starting the app.");
    if (await WriteSql(docker, FenceSql(captured, ledgerPath is null), Psql(), root) != 0)
        throw new InvalidOperationException("Assistance reconciliation failed.");
    if (await Run(docker, ["compose", "start", "app"], root) != 0)
    {
        try { stopConfirmed = await Run(docker, ["compose", "stop", "app"], root) == 0; }
        catch { stopConfirmed = false; }
        throw new InvalidOperationException(stopConfirmed ? "App restart failed; app was stopped again." : "App restart failed and app stop could not be confirmed.");
    }
    Console.WriteLine("Restore reconciled and app started. Assistance consent is disabled.");
    if (ledgerPath is null) Console.WriteLine("Usage continuity is unknown; today's allowance was exhausted. Review before re-enabling.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(stopConfirmed
        ? "App remains stopped. Resolve the error and run --reconcile with the saved ledger before starting it."
        : "Verify and stop the app immediately. Run --reconcile with the saved ledger before use.");
    if (ledgerPath != null) Console.Error.WriteLine($"Saved ledger: {ledgerPath}");
    return 1;
}

static string FenceSql(string ledger, bool unknown)
{
    string exhaust = unknown
        ? "INSERT INTO category_assistance.\"DailyUsage\" (\"UtcDay\", \"DispatchAttempts\") VALUES ((now() AT TIME ZONE 'UTC')::date, 5000) ON CONFLICT (\"UtcDay\") DO UPDATE SET \"DispatchAttempts\" = GREATEST(category_assistance.\"DailyUsage\".\"DispatchAttempts\", EXCLUDED.\"DispatchAttempts\");"
        : "";
    // Same transition and lock order as CategoryAssistanceRestoreFence.
    return $$"""
        BEGIN;
        SELECT 1 FROM taxonomy."Head" WHERE "Id"=1 FOR UPDATE;
        SELECT 1 FROM category_assistance."Settings" WHERE "Id"=1 FOR UPDATE;
        SELECT 1 FROM category_assistance."Connection" WHERE "Id"=1 FOR UPDATE;
        DO $$ BEGIN
          IF NOT EXISTS (SELECT 1 FROM taxonomy."Head" WHERE "Id"=1)
            OR NOT EXISTS (SELECT 1 FROM category_assistance."Settings" WHERE "Id"=1)
            OR NOT EXISTS (SELECT 1 FROM category_assistance."Connection" WHERE "Id"=1)
          THEN RAISE EXCEPTION 'Assistance fence singletons are missing'; END IF;
        END $$;
        CREATE TEMP TABLE captured_usage (day date PRIMARY KEY, attempts integer NOT NULL CHECK (attempts >= 0)) ON COMMIT DROP;
        COPY captured_usage (day, attempts) FROM STDIN WITH (FORMAT csv);
        {{ledger}}\.
        INSERT INTO category_assistance."DailyUsage" ("UtcDay", "DispatchAttempts")
          SELECT day, attempts FROM captured_usage
          ON CONFLICT ("UtcDay") DO UPDATE SET "DispatchAttempts" = GREATEST(category_assistance."DailyUsage"."DispatchAttempts", EXCLUDED."DispatchAttempts");
        {{exhaust}}
        WITH previous AS (SELECT to_jsonb(s) AS before, s."Revision" AS previous_revision FROM category_assistance."Settings" s WHERE s."Id"=1),
        changed AS (UPDATE category_assistance."Settings" s SET "Enabled"=false, "Revision"=gen_random_uuid(), "RestoreGeneration"=gen_random_uuid()
          WHERE s."Id"=1 RETURNING to_jsonb(s) AS after, s."Revision" AS revision)
        INSERT INTO category_assistance."SettingsAudit" ("Id", "Actor", "PreviousRevision", "Revision", "Before", "After", "RecordedAt")
          SELECT gen_random_uuid(), 'backup_restore', p.previous_revision, c.revision, p.before::text, c.after::text, now() FROM previous p CROSS JOIN changed c;
        UPDATE category_assistance."Connection" SET "Generation"=gen_random_uuid(), "Changing"=true, "ConfigurationFingerprint"='' WHERE "Id"=1;
        UPDATE category_assistance."Attempts" SET "State"=CASE WHEN "State"='dispatching' THEN 'indeterminate' ELSE 'stale' END,
          "ResultJson"=NULL, "ClaimToken"=NULL, "LeaseExpiresAt"=NULL, "NextEligibleAt"=NULL, "CompletedAt"=now()
          WHERE "State" IN ('queued', 'dispatching', 'suggested');
        DO $$ BEGIN
          IF NOT EXISTS (SELECT 1 FROM category_assistance."SettingsAudit" a
            JOIN category_assistance."Settings" s ON a."Revision"=s."Revision"
            WHERE a."Actor"='backup_restore' AND a."RecordedAt" >= transaction_timestamp() AND s."Id"=1)
          THEN RAISE EXCEPTION 'Assistance fence did not record an audit'; END IF;
        END $$;
        COMMIT;
        """;
}

static void Validate(string csv)
{
    var days = new HashSet<DateOnly>();
    foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] f = line.TrimEnd('\r').Split(',');
        if (f.Length != 2 || !DateOnly.TryParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || !int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int attempts) || attempts < 0 || !days.Add(day))
            throw new InvalidOperationException("Usage ledger is invalid or duplicated.");
    }
}
static async Task<string?> ReadSql(string docker, string sql, string[] a, string cwd)
{
    using var p = Process.Start(new ProcessStartInfo(docker, [.. a, "-A", "-t", "-c", sql])
        { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false }) ?? throw new InvalidOperationException("Could not start psql.");
    string output = await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    return p.ExitCode == 0 ? output : null;
}
static async Task<int> WriteSql(string docker, string sql, string[] a, string cwd)
{
    using var p = Process.Start(new ProcessStartInfo(docker, a)
        { WorkingDirectory = cwd, RedirectStandardInput = true, UseShellExecute = false }) ?? throw new InvalidOperationException("Could not start psql.");
    await p.StandardInput.WriteAsync(sql);
    p.StandardInput.Close();
    await p.WaitForExitAsync();
    return p.ExitCode;
}
static async Task<int> Run(string command, string[] a, string cwd)
{
    using var p = Process.Start(new ProcessStartInfo(command, a)
        { WorkingDirectory = cwd, UseShellExecute = false }) ?? throw new InvalidOperationException($"Could not start {command}.");
    await p.WaitForExitAsync();
    return p.ExitCode;
}
static string GetRoot()
{
    using var p = Process.Start(new ProcessStartInfo("git", ["rev-parse", "--show-toplevel"])
        { RedirectStandardOutput = true, UseShellExecute = false }) ?? throw new InvalidOperationException("Could not start git.");
    string root = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException("Could not find repository root.");
    return root;
}
static void LoadEnv(string path)
{
    if (!File.Exists(path)) return;
    foreach (string line in File.ReadLines(path))
    {
        string s = line.Trim();
        if (s.Length == 0 || s.StartsWith('#')) continue;
        int eq = s.IndexOf('=');
        if (eq > 0) Environment.SetEnvironmentVariable(s[..eq], s[(eq + 1)..].Trim('"').Trim('\''));
    }
}
