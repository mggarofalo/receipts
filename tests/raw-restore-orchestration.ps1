# Exercises the restore script with a process double. No Compose service is touched.
$ErrorActionPreference = 'Stop'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('receipts-restore-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    dotnet build tests/RestoreProcessDouble/RestoreProcessDouble.csproj -o $tempRoot -v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not build restore process double' }
    $env:RECEIPTS_RESTORE_DOCKER = Join-Path $tempRoot 'RestoreProcessDouble.exe'
    $env:RESTORE_TEST_LOG = Join-Path $tempRoot 'calls.log'
    $backup = Join-Path $tempRoot 'sample.sql.gz'
    $stream = [IO.File]::Create($backup)
    try {
        $zip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Compress)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes("SELECT 1;`n")
            $zip.Write($bytes, 0, $bytes.Length)
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }

    $env:RESTORE_TEST_STOP_FAIL = '1'
    'y' | dotnet run scripts/restore.cs -- $backup | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Failed stop was accepted' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG)
    if ($calls.Count -ne 1 -or $calls[0] -ne 'compose stop app') { throw 'Database was accessed after failed stop' }

    Set-Content -LiteralPath $env:RESTORE_TEST_LOG -Value ''
    $env:RESTORE_TEST_STOP_FAIL = '0'
    'y' | dotnet run scripts/restore.cs -- $backup | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Failed psql restore was accepted' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG | Where-Object { $_ })
    if ($calls[0] -ne 'compose stop app' -or ($calls -join ' ') -match 'compose start app') { throw 'App was started after failed restore' }
    if (-not (Get-ChildItem -LiteralPath $tempRoot -Filter '*.assistance-ledger-*.csv')) { throw 'Out-of-image ledger was not saved before restore' }

    # A damaged gzip stream must fail even when psql itself would accept the SQL.
    $env:RESTORE_TEST_RESTORE_OK = '1'
    $corrupt = Join-Path $tempRoot 'corrupt.sql.gz'
    [IO.File]::WriteAllBytes($corrupt, [byte[]](31, 139, 8, 0, 0, 0, 0, 0, 0, 3, 1))
    Set-Content -LiteralPath $env:RESTORE_TEST_LOG -Value ''
    'y' | dotnet run scripts/restore.cs -- $corrupt | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Corrupt gzip stream was accepted' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG | Where-Object { $_ })
    if (($calls -join ' ') -match 'compose start app') { throw 'App was started after corrupt gzip stream' }

    # A successful restore of an older image must stay stopped until migration and reconciliation.
    $env:RESTORE_TEST_PRESENT = '1'
    Set-Content -LiteralPath $env:RESTORE_TEST_LOG -Value ''
    'y' | dotnet run scripts/restore.cs -- $backup | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Missing assistance schema was accepted' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG | Where-Object { $_ })
    if (($calls -join ' ') -match 'compose start app') { throw 'Old schema was started before migration' }

    $env:RESTORE_TEST_SCHEMA = '1'
    $saved = Get-ChildItem -LiteralPath $tempRoot -Filter '*.assistance-ledger-*.csv' |
        Where-Object { $_.Name.StartsWith('sample.sql.gz.') -and ((Get-Content -LiteralPath $_.FullName -Raw) ?? '').Trim() -eq '2026-10-25,7' } |
        Select-Object -First 1
    if (-not $saved) { throw 'Missing saved ledger for reconciliation' }
    $env:RESTORE_TEST_FENCE_FAIL = '1'
    Set-Content -LiteralPath $env:RESTORE_TEST_LOG -Value ''
    'y' | dotnet run scripts/restore.cs -- --reconcile $saved.FullName | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Failed reconciliation was accepted' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG | Where-Object { $_ })
    if (($calls -join ' ') -match 'compose start app') { throw 'App started after failed reconciliation' }

    $env:RESTORE_TEST_FENCE_FAIL = '0'
    $env:RESTORE_TEST_START_FAIL = '1'
    Set-Content -LiteralPath $env:RESTORE_TEST_LOG -Value ''
    'y' | dotnet run scripts/restore.cs -- --reconcile $saved.FullName | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'Failed app restart was accepted' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG | Where-Object { $_ })
    if (($calls | Where-Object { $_ -eq 'compose stop app' }).Count -ne 2 -or
        ($calls | Where-Object { $_ -eq 'compose start app' }).Count -ne 1) {
        throw 'Failed restart did not trigger second stop'
    }

    $env:RESTORE_TEST_START_FAIL = '0'
    Set-Content -LiteralPath $env:RESTORE_TEST_LOG -Value ''
    'y' | dotnet run scripts/restore.cs -- --reconcile $saved.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Post-migration reconciliation did not start app' }
    $calls = @(Get-Content -LiteralPath $env:RESTORE_TEST_LOG | Where-Object { $_ })
    if (($calls | Where-Object { $_ -eq 'compose start app' }).Count -ne 1) {
        throw 'Successful reconciliation did not start app exactly once'
    }

    $maliciousLedger = Join-Path $tempRoot 'malicious.csv'
    Set-Content -LiteralPath $maliciousLedger -Value "2026-10-25,7`n\.; DROP TABLE public.foo;" -NoNewline
    $sql = dotnet run scripts/restore.cs -- --test-fence-sql $maliciousLedger 2>&1
    if ($LASTEXITCODE -eq 0 -or ($sql -join "`n") -match 'DROP TABLE') {
        throw 'Malformed usage ledger was admitted to SQL generation'
    }
    Write-Host 'Restore fail-closed orchestration proof passed.'
}
finally {
    Remove-Item Env:RECEIPTS_RESTORE_DOCKER, Env:RESTORE_TEST_LOG, Env:RESTORE_TEST_STOP_FAIL, Env:RESTORE_TEST_START_FAIL, Env:RESTORE_TEST_SCHEMA, Env:RESTORE_TEST_PRESENT, Env:RESTORE_TEST_FENCE_FAIL, Env:RESTORE_TEST_RESTORE_OK -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
