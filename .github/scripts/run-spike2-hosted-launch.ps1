param(
    [Parameter(Mandatory = $true)][ValidateSet('windows-2025', 'ubuntu-24.04')][string]$Runner,
    [Parameter(Mandatory = $true)][ValidateRange(1, 3)][int]$Launch,
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$MeasuredSourceSha
)

$ErrorActionPreference = 'Stop'
if ($MeasuredSourceSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'MeasuredSourceSha must be the frozen 40-character source commit SHA.'
}

$workflowRoot = (Resolve-Path $env:GITHUB_WORKSPACE).Path
$sourceRoot = (Resolve-Path $SourceRoot).Path
$workflowSha = $env:GITHUB_SHA
if ($workflowSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'GITHUB_SHA must identify the workflow commit.'
}
$actualWorkflowSha = (& git -C $workflowRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualWorkflowSha -ne $workflowSha) {
    throw "Workflow checkout '$actualWorkflowSha' does not match GITHUB_SHA '$workflowSha'."
}
$workflowFile = Join-Path $workflowRoot '.github/workflows/build.yml'
$workflowFileSha256 = (Get-FileHash -LiteralPath $workflowFile -Algorithm SHA256).Hash.ToLowerInvariant()

$actualSourceSha = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSourceSha -ne $MeasuredSourceSha) {
    throw "Measured source checkout '$actualSourceSha' does not match '$MeasuredSourceSha'."
}
$dirtyPaths = & git -C $sourceRoot status --porcelain --untracked-files=all
if ($LASTEXITCODE -ne 0 -or $dirtyPaths) {
    throw 'Measured source checkout must be clean after build.'
}

$savedWorkspace = $env:GITHUB_WORKSPACE
$savedGithubSha = $env:GITHUB_SHA
$savedWorkflowSha = $env:SPIKE_WORKFLOW_SHA
$savedWorkflowRoot = $env:SPIKE_WORKFLOW_ROOT
try {
    $env:SPIKE_WORKFLOW_SHA = $workflowSha
    $env:SPIKE_WORKFLOW_ROOT = $workflowRoot
    $env:GITHUB_WORKSPACE = $sourceRoot

    # The frozen 2A source predates split workflow/source provenance and expects
    # GITHUB_SHA to identify its checkout. Preserve the actual workflow SHA
    # separately, then stamp both identities into the resulting metadata.
    $env:GITHUB_SHA = $MeasuredSourceSha
    Push-Location $sourceRoot
    try {
        & (Join-Path $sourceRoot 'spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/scripts/run-hosted-launch.ps1') -Runner $Runner -Launch $Launch -ExperimentSha $MeasuredSourceSha
        if ($LASTEXITCODE -ne 0) {
            throw "Hosted launch failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:GITHUB_WORKSPACE = $savedWorkspace
    $env:GITHUB_SHA = $savedGithubSha
    $env:SPIKE_WORKFLOW_SHA = $savedWorkflowSha
    $env:SPIKE_WORKFLOW_ROOT = $savedWorkflowRoot
}

$runnerFolder = if ($Runner -eq 'windows-2025') { 'windows-2025' } else { 'ubuntu-24.04' }
$evidenceRoot = Join-Path $sourceRoot "artifacts/spike2-hosted/$runnerFolder/launch-$Launch"
$launchEvidenceRoot = Join-Path $evidenceRoot "launch-$Launch"
& (Join-Path $workflowRoot '.github/scripts/stamp-spike2-hosted-provenance.ps1') -EvidenceRoot $launchEvidenceRoot -WorkflowSha $workflowSha -MeasuredSourceSha $MeasuredSourceSha -WorkflowFileSha256 $workflowFileSha256
if ($LASTEXITCODE -ne 0) {
    throw "Hosted provenance stamping failed with exit code $LASTEXITCODE."
}
