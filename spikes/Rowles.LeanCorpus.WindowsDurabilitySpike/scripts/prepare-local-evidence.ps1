param(
    [Parameter(Mandatory = $true)][string]$ExperimentSha,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [string]$Hypervisor = 'unknown',
    [string]$HostOs = 'unknown',
    [string]$HostStorage = 'unknown',
    [string]$HostCachePolicy = 'unknown',
    [string]$GuestCachePolicy = 'unknown',
    [string]$VirtualDiskType = 'unknown',
    [string]$VirtualController = 'unknown',
    [string]$DiskBus = 'unknown',
    [string]$LibvirtCacheMode = 'unknown',
    [string]$LibvirtIoMode = 'unknown',
    [string]$DiscardMode = 'unknown',
    [string]$DetectZeroesMode = 'unknown',
    [string]$BackingStoreType = 'unknown',
    [string]$BackingStore = 'unknown',
    [string]$HostFilesystem = 'unknown',
    [string]$ResetMethod = 'unknown',
    [string]$SnapshotId = 'unknown'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Local evidence preparation must run on the disposable Windows guest.' }
if ($ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExperimentSha must be the frozen 40-character commit SHA.' }

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../')).Path
$project = 'spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/Rowles.LeanCorpus.WindowsDurabilitySpike.csproj'
$assembly = Join-Path $repositoryRoot 'artifacts/bin/Rowles.LeanCorpus.WindowsDurabilitySpike/release_net10.0/Rowles.LeanCorpus.WindowsDurabilitySpike.dll'
$root = [System.IO.Path]::GetFullPath($EvidenceRoot)
$data = [System.IO.Path]::GetFullPath($DataRoot)
if (Test-Path $root) { throw "Evidence root already exists and will not be replaced: $root" }
if (Test-Path $data) { throw "The disposable data root already exists and will not be reused: $data" }
if ($data.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
    $root.StartsWith($data + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence and measured index data must use separate directory trees.'
}

$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExperimentSha) { throw "Checkout SHA '$head' does not equal frozen SHA '$ExperimentSha'." }
if (& git -C $repositoryRoot status --porcelain) { throw 'The source checkout must be clean before the Release build.' }

Push-Location $repositoryRoot
try {
    & ./devops.ps1 build -Project $project -Framework net10.0
    if ($LASTEXITCODE -ne 0) { throw "The required Release build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
if (& git -C $repositoryRoot status --porcelain) { throw 'The source checkout became dirty during the Release build.' }
if (-not (Test-Path $assembly)) { throw "The Release assembly is missing: $assembly" }

New-Item -ItemType Directory -Path $root, (Join-Path $root 'logs'), (Join-Path $root 'traces'),
    (Join-Path $root 'reset-control'), (Join-Path $root 'hosted-replication') -Force | Out-Null
$env:SPIKE_EXPERIMENT_SHA = $ExperimentSha
$env:GITHUB_WORKSPACE = $repositoryRoot
$env:SPIKE_GITHUB_JOB = 'not_applicable_local_vm'

$collector = Join-Path $PSScriptRoot 'collect-windows-environment.ps1'
$collectorOutput = & $collector -ExperimentSha $ExperimentSha -Hypervisor $Hypervisor -HostOs $HostOs `
    -HostStorage $HostStorage -HostCachePolicy $HostCachePolicy -GuestCachePolicy $GuestCachePolicy `
    -VirtualDiskType $VirtualDiskType -VirtualController $VirtualController -DiskBus $DiskBus `
    -LibvirtCacheMode $LibvirtCacheMode -LibvirtIoMode $LibvirtIoMode -DiscardMode $DiscardMode `
    -DetectZeroesMode $DetectZeroesMode -BackingStoreType $BackingStoreType -BackingStore $BackingStore `
    -HostFilesystem $HostFilesystem -ResetMethod $ResetMethod -SnapshotId $SnapshotId
[System.IO.File]::WriteAllText((Join-Path $root 'windows-environment-collector.json'),
    ($collectorOutput | Out-String).Trim() + "`n", [System.Text.UTF8Encoding]::new($false))

function Invoke-Spike([string[]]$Arguments) {
    & dotnet $assembly @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Spike command '$($Arguments[0])' failed with exit code $LASTEXITCODE." }
}

Invoke-Spike @('validate-analysis-contract', '--output', (Join-Path $root 'logs/analysis-contract.txt'))
Invoke-Spike @('validate-observation-neutrality', '--output', (Join-Path $root 'instrumentation-audit.md'))
Invoke-Spike @('write-source-map', '--output', (Join-Path $root 'source-map.md'))
Invoke-Spike @('write-publication-semantics', '--output', (Join-Path $root 'publication-semantics.md'))
Invoke-Spike @('prepare-mechanism-order', '--profile', 'local', '--environment', 'windows',
    '--output', (Join-Path $root 'mechanism-order.csv'))
Invoke-Spike @('prepare-publication-order', '--output', (Join-Path $root 'publication-order.csv'))
Invoke-Spike @('write-dataset-identity', '--output', (Join-Path $root 'dataset-identity.json'),
    '--recovery-records', (Join-Path $root 'recovery-dataset.json'))
Invoke-Spike @('validate-recovery-dataset', '--dataset', (Join-Path $root 'recovery-dataset.json'))
$preflightScript = Join-Path $PSScriptRoot 'verify-hard-reset-prerequisites.ps1'
$preflight = & $preflightScript -ExpectedExperimentSha $ExperimentSha `
    -RecoveryDataset (Join-Path $root 'recovery-dataset.json') | Out-String
[System.IO.File]::WriteAllText((Join-Path $root 'logs/windows-preflight.json'),
    $preflight.Trim() + "`n", [System.Text.UTF8Encoding]::new($false))
Invoke-Spike @('validate-publication-candidates', '--data-root', $data,
    '--output', (Join-Path $root 'candidate-validation.json'))
Invoke-Spike @('capture-environment', '--environment-class', 'local_windows_vm', '--data-root', $data,
    '--order', (Join-Path $root 'mechanism-order.csv'), '--evidence', (Join-Path $root 'environment-capture'))

$capturedEnvironment = Join-Path $root 'environment-capture/environment.json'
$capturedHashes = Join-Path $root 'environment-capture/source-and-assembly-hashes.json'
Move-Item -LiteralPath $capturedEnvironment -Destination (Join-Path $root 'environment.json')
Move-Item -LiteralPath $capturedHashes -Destination (Join-Path $root 'source-and-assembly-hashes.json')
Remove-Item -LiteralPath (Join-Path $root 'environment-capture') -Force

Write-Output "Prepared clean local evidence root: $root"
Write-Output "Experiment SHA: $ExperimentSha"
Write-Output "Release assembly: $assembly"
