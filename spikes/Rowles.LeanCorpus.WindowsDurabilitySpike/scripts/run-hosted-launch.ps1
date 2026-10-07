param(
    [Parameter(Mandatory = $true)][ValidateSet('windows-2025', 'ubuntu-24.04')][string]$Runner,
    [Parameter(Mandatory = $true)][ValidateRange(1, 3)][int]$Launch,
    [Parameter(Mandatory = $true)][string]$ExperimentSha
)

$ErrorActionPreference = 'Stop'
if ($ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'ExperimentSha must be the frozen 40-character commit SHA.'
}
if ($env:GITHUB_SHA -ne $ExperimentSha) {
    throw "GITHUB_SHA '$($env:GITHUB_SHA)' does not equal experiment SHA '$ExperimentSha'."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../')).Path
$actualSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($actualSha -ne $ExperimentSha) {
    throw "Checked-out SHA '$actualSha' does not equal frozen experiment SHA '$ExperimentSha'."
}
$dirtyPaths = & git -C $repositoryRoot status --porcelain
if ($dirtyPaths) { throw 'Hosted measurement checkout is dirty after build.' }
$environment = if ($Runner -eq 'windows-2025') { 'windows' } else { 'ubuntu' }
$environmentClass = if ($Runner -eq 'windows-2025') { 'hosted_windows_2025' } else { 'hosted_ubuntu_24_04' }
$runnerFolder = if ($Runner -eq 'windows-2025') { 'windows-2025' } else { 'ubuntu-24.04' }
$evidenceRoot = Join-Path $repositoryRoot "artifacts/spike2-hosted/$runnerFolder/launch-$Launch"
$dataRoot = Join-Path $env:RUNNER_TEMP "leancorpus-spike2-$runnerFolder-launch-$Launch-data"
$assembly = Join-Path $repositoryRoot 'artifacts/bin/Rowles.LeanCorpus.WindowsDurabilitySpike/release_net10.0/Rowles.LeanCorpus.WindowsDurabilitySpike.dll'
$neutralityPath = Join-Path $evidenceRoot 'instrumentation-audit.md'
$orderPath = Join-Path $evidenceRoot 'mechanism-order.csv'

if (Test-Path $evidenceRoot) { throw "Evidence directory already exists and will not be replaced: $evidenceRoot" }
if (Test-Path $dataRoot) { throw "Data directory already exists and will not be reused: $dataRoot" }
New-Item -ItemType Directory -Path $evidenceRoot, $dataRoot -Force | Out-Null
$env:SPIKE_EXPERIMENT_SHA = $ExperimentSha
$env:SPIKE_GITHUB_JOB = "$($env:GITHUB_JOB)-launch-$Launch"
& (Join-Path $PSScriptRoot 'collect-hosted-environment.ps1')

try {
    & dotnet $assembly validate-observation-neutrality --output $neutralityPath
    if ($LASTEXITCODE -ne 0) { throw "Observation neutrality validation failed with exit code $LASTEXITCODE." }

    & dotnet $assembly prepare-mechanism-order --profile hosted --environment $environment --output $orderPath
    if ($LASTEXITCODE -ne 0) { throw "Mechanism order generation failed with exit code $LASTEXITCODE." }

    & dotnet $assembly run-mechanisms --profile hosted --environment-class $environmentClass `
        --data-root $dataRoot --evidence $evidenceRoot --order $orderPath `
        --neutrality-validation (Join-Path $evidenceRoot 'instrumentation-audit.json') --launch $Launch
    if ($LASTEXITCODE -ne 0) { throw "Mechanism launch failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path $dataRoot) {
        $failedTrials = Get-ChildItem -LiteralPath $dataRoot -Directory -Filter 'spike2-*' -ErrorAction SilentlyContinue
        if ($failedTrials.Count -gt 0) {
            $failedRoot = Join-Path $evidenceRoot 'failed-trials'
            New-Item -ItemType Directory -Path $failedRoot -Force | Out-Null
            foreach ($trial in $failedTrials) {
                Copy-Item -LiteralPath $trial.FullName -Destination $failedRoot -Recurse
            }
        }
    }
}
