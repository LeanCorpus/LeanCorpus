$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DevOpsUiObserveMenu {
    while ($true) {
        $item = [string](Read-SpectreSelection -Message 'Observe' -Choices @('Runs and artefacts', 'Diagnostics', 'Repository health', 'Back') -EnableSearch)
        switch ($item) {
            'Runs and artefacts' { Invoke-DevOpsUiRuns }
            'Diagnostics' { Invoke-DevOpsUiDiagnostics }
            'Repository health' { Invoke-DevOpsUiReport }
            default { return }
        }
    }
}

function Get-DevOpsUiRecentRuns {
    $repoRoot = Get-RepoRoot
    $runs = [System.Collections.Generic.List[object]]::new()
    foreach ($kind in @('test', 'coverage', 'diagnostics', 'benchmark')) {
        $root = Get-ArtifactRunsRoot -Kind $kind -RepoRoot $repoRoot
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            $manifestPath = Join-Path $directory.FullName 'run.json'
            if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
            try {
                $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32
                $started = [DateTimeOffset]::MinValue
                [void][DateTimeOffset]::TryParse([string]$manifest.startedAtUtc, [ref]$started)
                [void]$runs.Add([pscustomobject]@{
                    Kind = $kind; RunId = [string]$manifest.runId; Status = [string]$manifest.status
                    Started = $started; Target = [string]$manifest.target; Framework = [string]$manifest.framework
                    Directory = $directory.FullName; Manifest = $manifest
                })
            } catch { continue }
        }
    }
    return @($runs | Sort-Object Started -Descending | Select-Object -First 40)
}

function Invoke-DevOpsUiRuns {
    $runs = @(Get-DevOpsUiRecentRuns)
    if ($runs.Count -eq 0) { Write-SpectreHost '[yellow]No structured test, coverage, diagnostics or benchmark runs were found.[/]'; return }
    $rows = @($runs | ForEach-Object { [pscustomobject]@{
        Status = $_.Status; Kind = $_.Kind; Started = if ($_.Started -eq [DateTimeOffset]::MinValue) { 'unknown' } else { $_.Started.ToLocalTime().ToString('HH:mm dd MMM') }
        Target = $_.Target; Framework = $_.Framework; Run = $_.RunId
    } })
    Show-DevOpsUiSummaryTable -Rows $rows
    $selectedId = [string](Read-SpectreSelection -Message 'Select a run to inspect' -Choices @($runs | ForEach-Object { $_.RunId }) -EnableSearch)
    if (-not $selectedId) { return }
    $run = @($runs | Where-Object RunId -eq $selectedId | Select-Object -First 1)[0]
    $manifest = $run.Manifest
    Write-SpectreRule -Title "$($run.Kind) $($run.Status): $($run.RunId)" | Out-SpectreHost
    foreach ($property in @('branch', 'commit', 'dirty', 'os', 'architecture', 'machine', 'framework', 'configuration', 'target', 'commandLine', 'startedAtUtc', 'completedAtUtc', 'durationMs', 'error')) {
        if ($manifest.PSObject.Properties[$property] -and $null -ne $manifest.$property) { Write-SpectreHost ("{0}: {1}" -f $property, $manifest.$property) }
    }
    foreach ($name in @('summary', 'report', 'projects', 'outputs', 'warnings', 'failures')) {
        if ($manifest.PSObject.Properties[$name] -and $null -ne $manifest.$name) {
            Write-SpectreHost "$name`:`n$($manifest.$name | ConvertTo-Json -Depth 8)"
        }
    }
    $testReportPath = Join-Path $run.Directory 'report.json'
    if ($run.Kind -eq 'test' -and (Test-Path -LiteralPath $testReportPath -PathType Leaf)) {
        $testReport = Get-Content -LiteralPath $testReportPath -Raw | ConvertFrom-Json -Depth 32
        if ($null -ne $testReport.summary) {
            Show-DevOpsUiSummaryTable -Rows @([pscustomobject]@{
                Succeeded = $testReport.summary.succeeded
                Targets = "$($testReport.summary.passedTargetExecutions)/$($testReport.summary.requestedTargetExecutions)"
                Failed = $testReport.summary.failedTargetExecutions
                Crashed = $testReport.summary.crashedTargetExecutions
                Hung = $testReport.summary.hungTargetExecutions
                DurationSeconds = [Math]::Round([double]$testReport.summary.totalDurationMs / 1000, 2)
            })
        }
        $targetRows = @($testReport.targetResults)
        if ($targetRows.Count -gt 0) {
            Show-DevOpsUiSummaryTable -Rows @($targetRows | ForEach-Object { [pscustomobject]@{
                Iteration = $_.iteration; Suite = $_.suite; Target = $_.targetKey; Outcome = $_.outcome
                DurationSeconds = [Math]::Round([double]$_.durationMs / 1000, 2); FailedTests = $_.failedTestCount
            } })
        }
        foreach ($failure in @($testReport.tests | Where-Object { [int]$_.failedCount -gt 0 } | Select-Object -First 20)) {
            Write-SpectreHost (Get-SpectreEscapedText ("Failure: {0} | {1} | {2}" -f $failure.suite, $failure.testName, $failure.classification))
        }
    }
    Write-SpectreRule -Title 'Artefact tree' | Out-SpectreHost
    Format-SpectreTree -Data (New-DevOpsUiArtifactTreeNode -Path $run.Directory -RootPath $run.Directory) | Out-SpectreHost
    Write-SpectreHost (Get-SpectreEscapedText "Run directory: $($run.Directory)")
}

function New-DevOpsUiArtifactTreeNode {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$RootPath)
    $item = Get-Item -LiteralPath $Path -Force
    $children = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($directory in @(Get-ChildItem -LiteralPath $Path -Directory -Force -ErrorAction SilentlyContinue | Sort-Object Name)) {
        [void]$children.Add((New-DevOpsUiArtifactTreeNode -Path $directory.FullName -RootPath $RootPath))
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $Path -File -Force -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $name = [IO.Path]::GetRelativePath($RootPath, $file.FullName)
        [void]$children.Add(@{ Value = (Get-SpectreEscapedText ("{0} ({1} bytes)" -f $name, $file.Length)); Children = @() })
    }
    $label = if ([string]::Equals($Path, $RootPath, [StringComparison]::OrdinalIgnoreCase)) { $item.Name } else { $item.Name }
    return @{ Value = (Get-SpectreEscapedText $label); Children = @($children.ToArray()) }
}

function Get-DevOpsUiDotNetProcesses {
    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($process in Get-Process -ErrorAction SilentlyContinue | Sort-Object Id) {
        $processPath = ''
        try { $processPath = [string]$process.Path } catch { }
        $isDotNet = $process.ProcessName -match '^(dotnet|Rowles\.|LeanCorpus)' -or $processPath -match 'dotnet'
        if (-not $isDotNet) {
            try { $isDotNet = @($process.Modules | Where-Object { $_.ModuleName -match '^(coreclr\.dll|libcoreclr\.so|libcoreclr\.dylib)$' }).Count -gt 0 } catch { }
        }
        if ($isDotNet) {
            $pathLabel = if ($processPath) { [IO.Path]::GetFileName($processPath) } else { '(path unavailable)' }
            [void]$matches.Add([pscustomobject]@{ Id = [int]$process.Id; Name = [string]$process.ProcessName; Path = $processPath; Label = ("{0} ({1}) {2}" -f $process.ProcessName, $process.Id, $pathLabel) })
        }
    }
    return @($matches)
}

function Invoke-DevOpsUiDiagnostics {
    $workflow = [string](Read-SpectreSelection -Message 'Diagnostics workflow' -Choices @('Attach to a running .NET process', 'Download symbols for an artifact', 'Back'))
    if ($workflow -eq 'Back' -or -not $workflow) { return }
    if ($workflow -eq 'Download symbols for an artifact') {
        $artifact = Read-DevOpsUiText -Message 'Artifact path or URL'
        $null = Invoke-DevOpsUiCommand -Arguments @('diagnostics', 'symbols', $artifact) -Title 'Downloading diagnostic symbols'
        return
    }
    $processes = @(Get-DevOpsUiDotNetProcesses)
    if ($processes.Count -eq 0) { Write-SpectreHost '[yellow]No running .NET processes were discovered.[/]'; return }
    $process = Read-SpectreSelection -Message 'Select a running .NET process' -Choices $processes -ChoiceLabelProperty Label -EnableSearch
    if ($null -eq $process) { return }
    $action = [string](Read-SpectreSelection -Message "Diagnostics action for $($process.Name) ($($process.Id))" -Choices @('Counters', 'Trace', 'GC dump', 'Dump', 'Capture', 'Back'))
    if ($action -eq 'Back' -or -not $action) { return }
    $subcommand = switch ($action) { 'Counters' { 'counters' } 'Trace' { 'trace' } 'GC dump' { 'gcdump' } 'Dump' { 'dump' } 'Capture' { 'capture' } }
    $arguments = @('diagnostics', $subcommand, '--pid', [string]$process.Id)
    if ($action -eq 'Dump') {
        Write-SpectreHost '[yellow]A dump may contain sensitive application memory.[/]'
        $dumpType = [string](Read-SpectreSelection -Message 'Dump type' -Choices @('Mini', 'Heap', 'Full', 'Triage'))
        $arguments += @('--type', $dumpType)
    }
    if ($action -eq 'Capture') { $arguments += @('--duration', (Read-DevOpsUiText -Message 'Capture duration' -DefaultAnswer '5s')) }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments -Title "Capturing $action diagnostics for $($process.Name)"
}

function Invoke-DevOpsUiReport {
    $group = [string](Read-SpectreSelection -Message 'Repository health report' -Choices @('all', 'git', 'files', 'code'))
    $top = Read-DevOpsUiText -Message 'Entries per report list' -DefaultAnswer '10'
    $arguments = @('report', $group, '-Top', $top)
    if ($group -ne 'git') {
        $path = Read-DevOpsUiText -Message 'Optional file glob (blank includes all paths)' -AllowEmpty
        if ($path) { $arguments += @('-Path', $path) }
    }
    if (Read-DevOpsUiSwitch -Message 'Use strict health checks?') { $arguments += '-Strict' }
    if (Read-DevOpsUiSwitch -Message 'Emit JSON output?') { $arguments += '-Json' }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments -Title 'Generating repository health report'
}
