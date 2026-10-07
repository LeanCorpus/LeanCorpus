param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][string]$WorkflowSha,
    [Parameter(Mandatory = $true)][string]$MeasuredSourceSha,
    [Parameter(Mandatory = $true)][string]$WorkflowFileSha256
)

$ErrorActionPreference = 'Stop'
if ($WorkflowSha -notmatch '^[0-9a-fA-F]{40}$' -or $MeasuredSourceSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'WorkflowSha and MeasuredSourceSha must be full 40-character commit SHAs.'
}
if ($WorkflowFileSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'WorkflowFileSha256 must be a full SHA-256 value.'
}

$root = (Resolve-Path $EvidenceRoot).Path
$environmentPath = Join-Path $root 'environment.json'
$hashesPath = Join-Path $root 'source-and-assembly-hashes.json'
foreach ($path in @($environmentPath, $hashesPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Hosted provenance input is missing: $path"
    }
}

$environment = Get-Content -LiteralPath $environmentPath -Raw | ConvertFrom-Json -AsHashtable
$hashes = Get-Content -LiteralPath $hashesPath -Raw | ConvertFrom-Json -AsHashtable
if ($environment.experiment_sha -ne $MeasuredSourceSha -or $hashes.experiment_sha -ne $MeasuredSourceSha) {
    throw 'The built and measured source SHA does not match the requested frozen source SHA.'
}

foreach ($metadata in @($environment, $hashes)) {
    $metadata['experiment_sha'] = $MeasuredSourceSha
    $metadata['measured_source_sha'] = $MeasuredSourceSha
    $metadata['workflow_sha'] = $WorkflowSha
    $metadata['github_sha'] = $WorkflowSha
    $metadata['workflow_file_sha256'] = $WorkflowFileSha256.ToLowerInvariant()
}
$encoding = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($environmentPath, (ConvertTo-Json -InputObject $environment -Depth 100) + [Environment]::NewLine, $encoding)
[System.IO.File]::WriteAllText($hashesPath, (ConvertTo-Json -InputObject $hashes -Depth 100) + [Environment]::NewLine, $encoding)
