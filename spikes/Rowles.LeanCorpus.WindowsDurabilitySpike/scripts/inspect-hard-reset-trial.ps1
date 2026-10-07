param([Parameter(Mandatory = $true)][string]$ConfigFile)

$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json
if ($config.ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExperimentSha must be a frozen 40-character commit SHA.' }
$env:SPIKE_EXPERIMENT_SHA = [string]$config.ExperimentSha
$arguments = @(
    $config.AssemblyPath, 'recovery-inspect', '--trial-index', $config.TrialIndex,
    '--output', $config.InspectionPath, '--control', $config.ControlPath,
    '--trial-id', $config.TrialId, '--after-commit-return', ([string]$config.AfterCommitReturn).ToLowerInvariant(),
    '--dataset', $config.DatasetPath
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Read-only recovery inspection returned exit code $LASTEXITCODE." }
Get-Content -LiteralPath $config.InspectionPath -Raw
