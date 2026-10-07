param(
    [Parameter(Mandatory = $true)][ValidateSet('publication', 'mechanism')][string]$TraceType,
    [Parameter(Mandatory = $true)][string]$Candidate,
    [Parameter(Mandatory = $true)][string]$Representation,
    [Parameter(Mandatory = $true)][string]$ExperimentRoot,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [string]$CandidateValidation = '',
    [string]$PublicationSemantics = '',
    [Parameter(Mandatory = $true)][string]$NeutralityValidation,
    [Parameter(Mandatory = $true)][string]$ExperimentSha
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required for WPR trace capture.' }
if (-not $IsWindows) { throw 'Publication WPR capture must run on Windows.' }
if ($ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExperimentSha must be a frozen 40-character commit SHA.' }
if ($env:SPIKE_EXPERIMENT_SHA -and $env:SPIKE_EXPERIMENT_SHA -ne $ExperimentSha) { throw 'SPIKE_EXPERIMENT_SHA does not match the requested source SHA.' }

$root = [System.IO.Path]::GetFullPath($ExperimentRoot)
if ($TraceType -eq 'publication') {
    if ($Candidate -notin @('P0', 'P1', 'P2') -or $Representation -notin @('loose', 'compound')) {
        throw 'Publication traces require P0/P1/P2 and loose/compound.'
    }
    $cellId = "$Candidate-$Representation"
} else {
    if ($Candidate -notin @('A_current_full', 'D_leancorpus_wrapper') -or
        $Representation -notmatch '^(8MiB-16|82MiB-64)$') {
        throw 'Mechanism traces require A/D and the declared representative payload/file-count pairs.'
    }
    $cellId = "mechanism-$Candidate-$Representation"
}
$traceRoot = Join-Path $root 'traces'
$cellRoot = Join-Path $traceRoot 'cells'
$cellEvidence = Join-Path $cellRoot $cellId
$tracePath = Join-Path $traceRoot "$cellId.etl"
$indexPath = Join-Path $root 'trace-index.csv'
$assembly = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../../../')).Path 'artifacts/bin/Rowles.LeanCorpus.WindowsDurabilitySpike/release_net10.0/Rowles.LeanCorpus.WindowsDurabilitySpike.dll'
$cellManifest = Join-Path $cellEvidence 'trace-cell.json'
$wpr = Get-Command 'wpr.exe' -ErrorAction SilentlyContinue
$success = $false
$sequenceValidation = 'not_run'
$expectedCreate = 0
$expectedFlush = 0
$sequence = 'unavailable'
$errorText = ''
$recording = $false

New-Item -ItemType Directory -Path $traceRoot, $cellRoot -Force | Out-Null
if (Test-Path $cellEvidence) { throw "Trace cell evidence already exists: $cellEvidence" }
if (Test-Path $tracePath) { throw "ETL trace already exists and will not be replaced: $tracePath" }
if (Test-Path $indexPath) {
    $priorRows = Import-Csv -LiteralPath $indexPath
    if ($priorRows.cell_id -contains $cellId) { throw "Trace index already contains $cellId; this cell will not be repeated." }
}
$env:SPIKE_EXPERIMENT_SHA = $ExperimentSha

try {
    if (-not $wpr) { throw 'Windows Performance Recorder (wpr.exe) is unavailable.' }
    $profileText = (& $wpr.Source -profiles 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "wpr -profiles failed: $profileText" }
    if ($profileText -notmatch '(?im)^\s*FileIO(?:\.|\s|$)') {
        throw 'The WPR FileIO recording profile is not present on this guest.'
    }
    $recording = $true
    & $wpr.Source -start 'FileIO' -filemode
    if ($LASTEXITCODE -ne 0) { throw "wpr -start FileIO failed with exit code $LASTEXITCODE." }

    if ($TraceType -eq 'publication') {
        & dotnet $assembly trace-publication-cell --candidate $Candidate --representation $Representation `
            --data-root $DataRoot --evidence $cellEvidence --candidate-validation $CandidateValidation `
            --semantics $PublicationSemantics --neutrality-validation $NeutralityValidation
    } else {
        $parts = $Representation -split '-'
        $payloadBytes = if ($parts[0] -eq '8MiB') { 8388608 } else { 85983232 }
        $fileCount = [int]$parts[1]
        & dotnet $assembly trace-mechanism-cell --variant $Candidate --payload-bytes $payloadBytes `
            --file-count $fileCount --data-root $DataRoot --evidence $cellEvidence `
            --neutrality-validation $NeutralityValidation
    }
    if ($LASTEXITCODE -ne 0) { throw "Instrumented trace-cell run failed with exit code $LASTEXITCODE." }

    & $wpr.Source -stop $tracePath "Spike 2 publication trace $cellId"
    $recording = $false
    if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path $tracePath)) { throw 'WPR reported success but did not create the ETL trace.' }
    $manifest = Get-Content -LiteralPath $cellManifest -Raw | ConvertFrom-Json
    if (-not $manifest.event_order_passed) { throw 'The instrumented publication event ordering did not pass.' }
    $expectedCreate = [int]$manifest.expected_create_file_count
    $expectedFlush = [int]$manifest.expected_flush_filebuffers_count
    $sequence = [string]$manifest.observed_call_sequence_summary
    $sequenceValidation = 'passed'
    $success = $true
}
catch {
    $errorText = $_.Exception.Message
}
finally {
    if ($recording -and $wpr) {
        & $wpr.Source -cancel | Out-Null
    }
}

if (-not (Test-Path $cellEvidence)) {
    New-Item -ItemType Directory -Path $cellEvidence -Force | Out-Null
}
$traceName = if (Test-Path $tracePath) { [System.IO.Path]::GetRelativePath($root, $tracePath).Replace('\', '/') } else { '' }
$sha = if (Test-Path $tracePath) { (Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
$fields = @($cellId, $traceName, $sha, $Candidate, $Representation, $expectedCreate, $expectedFlush, $sequence,
    $sequenceValidation, $success.ToString().ToLowerInvariant(), $errorText)
$escaped = foreach ($field in $fields) {
    $value = [string]$field
    if ($value -match '[,"\r\n]') { '"' + $value.Replace('"', '""') + '"' } else { $value }
}
$line = $escaped -join ','
$header = 'cell_id,trace_file,sha256,candidate_id,representation,expected_createfile_count,expected_flushfilebuffers_count,observed_call_sequence_summary,sequence_validation,success,error'
if (-not (Test-Path $indexPath)) {
    [System.IO.File]::WriteAllText($indexPath, $header + "`n", [System.Text.UTF8Encoding]::new($false))
}
[System.IO.File]::AppendAllText($indexPath, $line + "`n", [System.Text.UTF8Encoding]::new($false))

if (-not $success) {
    Write-Error "Trace capture for $cellId is incomplete: $errorText"
    exit 1
}
Write-Output "Captured $traceName ($sha); $sequence"
