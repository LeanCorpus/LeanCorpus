Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$module = Import-Module (Join-Path $PSScriptRoot '../DevOps.psm1') -Force -PassThru
$root = Join-Path ([IO.Path]::GetTempPath()) ('leancorpus-progress-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    foreach ($mode in @('absent', 'enabled', 'unwritable')) {
        $eventPath = if ($mode -eq 'unwritable') { $root } else { Join-Path $root "$mode.jsonl" }
        $options = [pscustomobject]@{}
        if ($mode -ne 'absent') { $options | Add-Member UiEventPath $eventPath }
        $context = [pscustomobject]@{ Options = $options }
        # Return the real closure to the caller, outside the owning module.
        $callback = & $module {
            param($context)
            New-TestProgressCallback -Context $context -Target ([pscustomobject]@{ Key = 'core/net10.0' }) `
                -Iteration 2 -ExecutionNumber 3 -ExecutionCount 4
        } $context
        & $callback $null ([TimeSpan]::FromSeconds(30))
        $result = & $module {
            param($callback, $root)
            Invoke-ProcessWithLifecycle -FileName (Get-Command pwsh).Source `
                -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 3; Write-Output child-finished; exit 7') `
                -StdOutPath (Join-Path $root 'stdout.txt') -StdErrPath (Join-Path $root 'stderr.txt') `
                -CaptureOutput $true -ProgressIntervalSeconds 1 -OnProgress $callback
        } $callback $root
        if ($result.ExitCode -ne 7 -or $result.WasKilled) { throw "Progress callback killed the child in mode '$mode'." }
        if (-not (Get-Content (Join-Path $root 'stdout.txt') -Raw).Contains('child-finished')) {
            throw "Progress callback lost child output in mode '$mode'."
        }
        if ($mode -eq 'enabled') {
            $events = @(Get-Content $eventPath | ConvertFrom-Json)
            if ($events.Count -lt 2 -or $events[0].name -ne 'TargetProgress' `
                -or $events[0].data.target -ne 'core/net10.0' -or $events[0].data.elapsedMs -ne 30000 `
                -or $events[0].data.iteration -ne 2 -or $events[0].data.execution -ne 3) {
                throw 'Progress callback lost event data.'
            }
        }
    }
    Write-Host 'Test progress smoke passed: callbacks retain module scope and children finish in all three event modes.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
    Remove-Module $module
}
