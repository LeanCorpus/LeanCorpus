$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../DevOps.psm1') -Force

& (Get-Module DevOps) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('lc-backup-matrix-validation-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($root)
    $manifestPath = Join-Path $root source.json
    $filePath = Join-Path $root fixture.cs
    [IO.File]::WriteAllText($filePath, 'alpha')
    $entry = @{ path = 'fixture.cs'; sha256 = '8ed3f6ad685b959ead7022518e1af76cd816f8e8ec7ccdda1ed4018e8f2223f8' }
    $commit = '0000000000000000000000000000000000000000'
    function Write-FixtureManifest($entries, $fingerprint = '') {
        $value = @{ commit = $commit; files = @($entries) }
        if ($fingerprint) { $value.fingerprint = $fingerprint }
        [IO.File]::WriteAllText($manifestPath, ($value | ConvertTo-Json -Depth 5))
    }
    function Assert-ManifestRejected {
        $rejected = $false
        try { $null = Get-BackupRestoreSourceManifest -RepoRoot $root -ManifestPath $manifestPath }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'A malformed or mismatched source manifest was accepted.' }
    }
    try {
        Write-FixtureManifest @($entry)
        $valid = Get-BackupRestoreSourceManifest -RepoRoot $root -ManifestPath $manifestPath
        if ($valid.files.Count -ne 1 -or $valid.commit -ne $commit) { throw 'Valid source inventory was not retained.' }
        Write-FixtureManifest @($entry) $valid.fingerprint
        $null = Get-BackupRestoreSourceManifest -RepoRoot $root -ManifestPath $manifestPath
        Write-FixtureManifest @(); Assert-ManifestRejected
        Write-FixtureManifest @($entry, $entry); Assert-ManifestRejected
        Write-FixtureManifest @(@{ path = '../fixture.cs'; sha256 = $entry.sha256 }); Assert-ManifestRejected
        Write-FixtureManifest @($entry) ('0' * 64); Assert-ManifestRejected
        Write-FixtureManifest @($entry)
        [IO.File]::WriteAllText($filePath, 'changed'); Assert-ManifestRejected
        [IO.File]::WriteAllText($filePath, 'alpha')

        # Only the external test runner is replaced; source verification and environment cleanup are real.
        $script:fixtureRoot = $root
        $script:runnerCalls = 0
        $script:completionStatus = ''
        function Get-RepoRoot { return $script:fixtureRoot }
        function New-ArtifactRun {
            param($Kind, $Framework, $Configuration, $Target, $RepoRoot, $CommandLine)
            return [pscustomobject]@{ RunDirectory = (Join-Path $script:fixtureRoot run) }
        }
        function Invoke-DevOpsTest { param([string[]]$Arguments); $script:runnerCalls++; return 1 }
        function Complete-ArtifactRun {
            param($RunDirectory, $Status, $AdditionalValues)
            $script:completionStatus = $Status
        }
        $names = @('LEANCORPUS_BACKUP_RESTORE_MEASUREMENT', 'LEANCORPUS_BACKUP_COMPOUND',
            'LEANCORPUS_BACKUP_SOURCE_DURABLE', 'LEANCORPUS_BACKUP_TEMP_ROOT',
            'LEANCORPUS_BACKUP_SOURCE_COMMIT', 'LEANCORPUS_BACKUP_SOURCE_FINGERPRINT', 'CHAOS_ITERATIONS')
        $previous = @{}
        foreach ($name in $names) {
            $previous[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, "prior-$name")
        }
        try {
            $failed = $false
            try { $null = Invoke-BackupRestoreMatrix -Arguments @('-SourceManifest', $manifestPath, '-Count', '1', '-Iterations', '1') }
            catch { $failed = $true }
            if (-not $failed -or $script:runnerCalls -ne 1 -or $script:completionStatus -ne 'Failed') {
                throw 'Failed child execution was not preserved as a failed matrix.'
            }
            foreach ($name in $names) {
                if ([Environment]::GetEnvironmentVariable($name) -ne "prior-$name") {
                    throw "Matrix failed to restore environment: $name"
                }
            }
        } finally {
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
        }
        Write-Host 'Backup/restore matrix source and failure-path validation passed.'
    } finally {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
