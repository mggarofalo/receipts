# Disposable PostgreSQL proof for scripts/restore.cs's reconciliation SQL.
# Run from the repository root: pwsh -File tests/raw-restore-fence.ps1
$ErrorActionPreference = 'Stop'
$container = 'receipts-restore-proof-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$ledger = [System.IO.Path]::GetTempFileName()
try {
    Set-Content -LiteralPath $ledger -Value "2026-10-25,7`n2026-10-26,3`n" -NoNewline
    docker run --name $container -e POSTGRES_PASSWORD=test -d pgvector/pgvector:0.8.7-pg17 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start disposable PostgreSQL' }
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        docker exec $container pg_isready -U postgres | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready' }
    Start-Sleep -Seconds 2
    $setup = @'
CREATE SCHEMA taxonomy;
CREATE TABLE taxonomy."Head" ("Id" integer PRIMARY KEY);
INSERT INTO taxonomy."Head" VALUES (1);
CREATE SCHEMA category_assistance;
CREATE TABLE category_assistance."Settings" ("Id" integer PRIMARY KEY, "Enabled" boolean, "Provider" text, "Model" text, "DailyLimit" integer, "PendingLimit" integer, "Revision" uuid, "RestoreGeneration" uuid);
INSERT INTO category_assistance."Settings" VALUES (1, true, 'jev', 'jev-1.14.0', 10, 3, gen_random_uuid(), gen_random_uuid());
CREATE TABLE category_assistance."Connection" ("Id" integer PRIMARY KEY, "Generation" uuid, "Changing" boolean, "ConfigurationFingerprint" text);
INSERT INTO category_assistance."Connection" VALUES (1, gen_random_uuid(), false, 'bound');
CREATE TABLE category_assistance."DailyUsage" ("UtcDay" date PRIMARY KEY, "DispatchAttempts" integer NOT NULL);
INSERT INTO category_assistance."DailyUsage" VALUES ('2026-10-25', 4), ('2026-10-26', 5);
CREATE TABLE category_assistance."SettingsAudit" ("Id" uuid PRIMARY KEY, "Actor" text, "PreviousRevision" uuid, "Revision" uuid, "Before" text, "After" text, "RecordedAt" timestamptz);
CREATE TABLE category_assistance."Attempts" ("Id" integer PRIMARY KEY, "State" text, "ResultJson" text, "ClaimToken" uuid, "LeaseExpiresAt" timestamptz, "NextEligibleAt" timestamptz, "CompletedAt" timestamptz);
INSERT INTO category_assistance."Attempts" ("Id", "State", "ResultJson", "ClaimToken") VALUES
  (1, 'queued', 'old', gen_random_uuid()), (2, 'dispatching', 'old', gen_random_uuid()), (3, 'suggested', 'old', gen_random_uuid()), (4, 'abstained', 'keep', NULL);
'@
    $setup | docker exec -i $container psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres -q
    if ($LASTEXITCODE -ne 0) { throw 'Fixture setup failed' }
    $before = docker exec $container psql -X -At -U postgres -d postgres -c 'SELECT "Revision" || ''|'' || "RestoreGeneration" FROM category_assistance."Settings"; SELECT "Generation" FROM category_assistance."Connection";'
    $sql = dotnet run scripts/restore.cs -- --test-fence-sql $ledger
    if ($LASTEXITCODE -ne 0) { throw 'Fence SQL generation failed' }
    $sql | docker exec -i $container psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres -q
    if ($LASTEXITCODE -ne 0) { throw 'Fence SQL execution failed' }
    $after = docker exec $container psql -X -At -U postgres -d postgres -c 'SELECT "Enabled" || ''|'' || "Revision" || ''|'' || "RestoreGeneration" FROM category_assistance."Settings"; SELECT "Generation" || ''|'' || "Changing" || ''|'' || "ConfigurationFingerprint" FROM category_assistance."Connection"; SELECT "UtcDay" || ''|'' || "DispatchAttempts" FROM category_assistance."DailyUsage" ORDER BY "UtcDay"; SELECT "Id" || ''|'' || "State" || ''|'' || COALESCE("ResultJson", ''null'') || ''|'' || COALESCE("ClaimToken"::text, ''null'') FROM category_assistance."Attempts" ORDER BY "Id"; SELECT "Actor" FROM category_assistance."SettingsAudit";'
    if ($LASTEXITCODE -ne 0) { throw 'Fence verification query failed' }
    $rows = @($after)
    if ($rows.Count -ne 9) { throw "Unexpected verification rows: $($rows -join ', ')" }
    if ($rows[0] -notmatch '^false\|' -or $rows[0].Contains($before[0].Split('|')[1]) -or $rows[0].Contains($before[0].Split('|')[0])) { throw 'Consent or settings generations not fenced' }
    if ($rows[1] -notmatch '\|true\|$' -or $rows[1].Contains($before[1])) { throw 'Connection not rotated and marked changing' }
    if ($rows[2] -ne '2026-10-25|7' -or $rows[3] -ne '2026-10-26|5') { throw 'Daily usage max merge failed' }
    if ($rows[4] -ne '1|stale|null|null' -or $rows[5] -ne '2|indeterminate|null|null' -or $rows[6] -ne '3|stale|null|null') { throw 'Outstanding attempts not invalidated' }
    if ($rows[7] -ne '4|abstained|keep|null') { throw 'Completed attempt was altered' }
    if ($rows[8] -ne 'backup_restore') { throw 'Restore audit actor missing' }
    $audits = docker exec $container psql -X -At -U postgres -d postgres -c 'SELECT count(*) FROM category_assistance."SettingsAudit" WHERE "Actor" = ''backup_restore'';'
    if ($LASTEXITCODE -ne 0 -or $audits -ne '1') { throw 'Restore audit missing' }
    Write-Host 'Disposable PostgreSQL fence proof passed.'
}
finally {
    docker rm -f $container | Out-Null
    Remove-Item -LiteralPath $ledger -Force
}
