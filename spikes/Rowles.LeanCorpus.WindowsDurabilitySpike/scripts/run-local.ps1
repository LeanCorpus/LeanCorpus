param(
    [Parameter(Mandatory = $true)][ValidateSet('mechanism-launch', 'publication-launch', 'process-crash', 'fault-injection', 'analyse-mechanisms', 'traces')][string]$Stage,
    [Parameter(Mandatory = $true)][string]$ExperimentSha,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [ValidateRange(1, 5)][int]$Launch = 1
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required for Spike 2 local collection.' }
if (-not $IsWindows) { throw 'Local Windows collection must run inside the disposable Windows guest.' }
$root = [System.IO.Path]::GetFullPath($EvidenceRoot)
$data = [System.IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path $root)) { throw "Prepared evidence root is missing: $root" }
if (-not (Test-Path $data)) { throw "Prepared disposable data root is missing: $data" }

$environmentScript = Join-Path $PSScriptRoot 'set-spike-environment.ps1'
. $environmentScript -ExperimentSha $ExperimentSha -EvidenceRoot $root
$assembly = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../../../')).Path `
    'artifacts/bin/Rowles.LeanCorpus.WindowsDurabilitySpike/release_net10.0/Rowles.LeanCorpus.WindowsDurabilitySpike.dll'
if (-not (Test-Path $assembly)) { throw "Frozen Release assembly is missing: $assembly" }
$neutrality = Join-Path $root 'instrumentation-audit.json'
$mechanismOrder = Join-Path $root 'mechanism-order.csv'
$publicationOrder = Join-Path $root 'publication-order.csv'

function Invoke-Spike([string[]]$Arguments) {
    & dotnet $assembly @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Spike command '$($Arguments[0])' failed with exit code $LASTEXITCODE." }
}

switch ($Stage) {
    'mechanism-launch' {
        Invoke-Spike @('run-mechanisms', '--profile', 'local', '--environment-class', 'local_windows_vm',
            '--data-root', $data, '--evidence', (Join-Path $root 'mechanism'), '--order', $mechanismOrder,
            '--neutrality-validation', $neutrality, '--launch', [string]$Launch)
    }
    'publication-launch' {
        Invoke-Spike @('run-publication', '--data-root', $data,
            '--evidence', (Join-Path $root 'publication'), '--order', $publicationOrder,
            '--candidate-validation', (Join-Path $root 'candidate-validation.json'),
            '--semantics', (Join-Path $root 'publication-semantics.md'),
            '--neutrality-validation', $neutrality, '--launch', [string]$Launch)
    }
    'process-crash' {
        Invoke-Spike @('recover', '--data-root', $data,
            '--evidence', (Join-Path $root 'process-recovery'), '--neutrality-validation', $neutrality,
            '--dataset', (Join-Path $root 'recovery-dataset.json'))
    }
    'fault-injection' {
        Invoke-Spike @('run-fault-injection', '--data-root', $data,
            '--evidence', (Join-Path $root 'fault-injection'), '--neutrality-validation', $neutrality,
            '--dataset', (Join-Path $root 'recovery-dataset.json'))
    }
    'analyse-mechanisms' {
        $summary = Join-Path $root 'mechanism-summary.md'
        if (Test-Path $summary) { throw "Mechanism summary already exists and will not be replaced: $summary" }
        Invoke-Spike @('analyse-mechanisms', '--input', (Join-Path $root 'mechanism'), '--output', $summary)
    }
    'traces' {
        $traceScript = Join-Path $PSScriptRoot 'capture-publication-trace.ps1'
        $neutralityPath = Join-Path $root 'instrumentation-audit.md'
        $candidateValidation = Join-Path $root 'candidate-validation.json'
        $semantics = Join-Path $root 'publication-semantics.md'
        $cells = @(
            @{ type = 'mechanism'; candidate = 'A_current_full'; representation = '8MiB-16' },
            @{ type = 'mechanism'; candidate = 'D_leancorpus_wrapper'; representation = '8MiB-16' },
            @{ type = 'mechanism'; candidate = 'A_current_full'; representation = '82MiB-64' },
            @{ type = 'mechanism'; candidate = 'D_leancorpus_wrapper'; representation = '82MiB-64' }
        )
        foreach ($candidate in @('P0', 'P1', 'P2')) {
            foreach ($representation in @('loose', 'compound')) {
                $cells += @{ type = 'publication'; candidate = $candidate; representation = $representation }
            }
        }
        foreach ($cell in $cells) {
            $safeCell = ($cell.candidate + '-' + $cell.representation) -replace '[^A-Za-z0-9-]', '-'
            $cellDataRoot = Join-Path $data ("trace-data-" + $safeCell)
            $traceArguments = @{
                TraceType = $cell.type
                Candidate = $cell.candidate
                Representation = $cell.representation
                ExperimentRoot = $root
                DataRoot = $cellDataRoot
                NeutralityValidation = $neutralityPath
                ExperimentSha = $ExperimentSha
            }
            if ($cell.type -eq 'publication') {
                $traceArguments.CandidateValidation = $candidateValidation
                $traceArguments.PublicationSemantics = $semantics
            }
            & $traceScript @traceArguments
            if ($LASTEXITCODE -ne 0) { throw "WPR trace failed for $($cell.candidate)-$($cell.representation)." }
        }
    }
}
