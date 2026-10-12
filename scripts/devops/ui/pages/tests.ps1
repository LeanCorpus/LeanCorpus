$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-DevOpsUiTestSuiteChoices {
    $registryPath = Join-Path (Get-RepoRoot) 'scripts/devops/config/test-suites.psd1'
    $registry = Import-PowerShellDataFile $registryPath
    $groups = [ordered]@{
        'Core and shared' = [System.Collections.Generic.List[object]]::new()
        'Server' = [System.Collections.Generic.List[object]]::new()
        'Providers' = [System.Collections.Generic.List[object]]::new()
    }
    foreach ($key in @($registry.Keys | Sort-Object)) {
        $entry = $registry[$key]
        $group = if ($key -like 'server-orchard*') { 'Providers' } elseif ($key -like 'server-*') { 'Server' } else { 'Core and shared' }
        [void]$groups[$group].Add([pscustomobject]@{ Key = [string]$key; Label = ("{0} ({1})" -f $entry.Name, $key) })
    }
    $choices = @(
        foreach ($group in $groups.Keys) {
            @{ Name = $group; Choices = @($groups[$group].ToArray()) }
        }
    )
    return ,$choices
}

function Read-DevOpsUiTestOptions {
    param([switch]$ForceAffected)
    $scopeChoices = if ($ForceAffected) { @('Affected source changes', 'Back') } else { @('Manual suites', 'Affected source changes', 'All registered suites', 'Back') }
    $scope = [string](Read-DevOpsUiSelection -Message 'Test selection scope' -Choices $scopeChoices)
    if ($scope -eq 'Back' -or -not $scope) { return $null }
    $suiteKeys = @()
    $commit = ''
    $range = ''
    switch ($scope) {
        'Manual suites' {
            $grouped = Get-DevOpsUiTestSuiteChoices
            $selected = @(Read-DevOpsUiGroupedSelection -Message 'Select test suites' -Choices $grouped `
            -ChoiceLabelProperty 'Label' -PageSize 12)
            $suiteKeys = @($selected | ForEach-Object { if ($_.PSObject.Properties['Key']) { [string]$_.Key } else { [string]$_ } })
            if ($suiteKeys.Count -eq 0) { return $null }
        }
        'Affected source changes' {
            $suiteKeys = @('affected')
            $sourceMode = [string](Read-DevOpsUiSelection -Message 'Affected change source' -Choices @('Working tree', 'Commit', 'Git range'))
            if ($sourceMode -eq 'Commit') { $commit = Read-DevOpsUiText -Message 'Commit to inspect' -DefaultAnswer 'HEAD' }
            if ($sourceMode -eq 'Git range') { $range = Read-DevOpsUiText -Message 'Git range, for example HEAD~3..HEAD' -DefaultAnswer 'HEAD~1..HEAD' }
        }
        'All registered suites' { $suiteKeys = @('all') }
    }

    $suiteRegistry = Import-PowerShellDataFile (Join-Path (Get-RepoRoot) 'scripts/devops/config/test-suites.psd1')
    $commonSuites = if ($suiteKeys[0] -eq 'all') { @($suiteRegistry.Keys) } else { @($suiteKeys | Where-Object { $_ -ne 'affected' }) }
    if ($suiteKeys.Count -eq 1 -and $suiteKeys[0] -eq 'all') {
        $frameworks = @('Automatic suite defaults')
    } elseif ($scope -eq 'Affected source changes') {
        $frameworks = @('Automatic suite defaults', 'net10.0', 'net11.0')
    } else {
        $frameworks = @(Get-DevOpsUiFrameworkChoices -SuiteKeys $commonSuites)
    }
    $framework = [string](Read-DevOpsUiSelection -Message 'Target framework' -Choices $frameworks)
    if (-not $framework) { return $null }
    if ($framework -eq 'Automatic suite defaults') { $framework = '' }
    $configuration = [string](Read-DevOpsUiSelection -Message 'Build configuration' -Choices @('Release', 'Debug'))

    $areaValues = Read-DevOpsUiEnumValues -EnumName TestArea
    $categoryValues = Read-DevOpsUiEnumValues -EnumName TestCategory
    $techniqueValues = Read-DevOpsUiEnumValues -EnumName TestTechnique
    $areas = @(Read-DevOpsUiMultiSelection -Message 'Areas (OR within areas)' -Choices $areaValues)
    $categories = @(Read-DevOpsUiMultiSelection -Message 'Categories (OR within categories)' -Choices $categoryValues)
    $techniques = @(Read-DevOpsUiMultiSelection -Message 'Techniques (OR within techniques)' -Choices $techniqueValues)

    $runnerFilter = Read-DevOpsUiText -Message 'Additional xUnit filter (optional)' -AllowEmpty
    $runMode = Read-DevOpsUiSelection -Message 'Repetition mode' -Choices @('Standard run', 'Flaky stress preset (30 runs by default)')
    $isFlaky = $runMode -like 'Flaky*'
    do {
        $countText = Read-DevOpsUiText -Message 'Sequential run count' -DefaultAnswer $(if ($isFlaky) { '30' } else { '1' })
        $countValue = 0
        $validCount = [int]::TryParse($countText, [ref]$countValue) -and $countValue -gt 0
        if (-not $validCount) { Write-DevOpsUiMarkup '[yellow]Enter a positive whole number.[/]' }
    } until ($validCount)
    $profile = [string](Read-DevOpsUiSelection -Message 'Parallel execution profile' -Choices @('unit', 'integration', 'stress'))
    $hangTimeout = Read-DevOpsUiTimeout -Message 'Hang detection timeout' -DefaultAnswer '100s'
    $processTimeout = Read-DevOpsUiTimeout -Message 'Outer process timeout' -DefaultAnswer 'off'
    $verbosity = [string](Read-DevOpsUiSelection -Message 'Runner output verbosity' -Choices @('quiet', 'minimal', 'normal', 'detailed'))
    $runtimeIdentifier = ''
    if ($suiteKeys -contains 'aot' -or $suiteKeys -contains 'all') {
        $runtimeIdentifier = Read-DevOpsUiText -Message 'Native AOT runtime identifier (blank auto-detects)' -AllowEmpty
    }

    Show-DevOpsUiHeader -Page 'Test options' -Description 'Space toggles an option. Enter continues. Leave all options clear for a normal run.'
    $flagChoices = @(
        [pscustomobject]@{ Key = 'Diagnostics'; Label = 'Diagnostics | Capture traces and runner diagnostics' },
        [pscustomobject]@{ Key = 'CollectCoverage'; Label = 'CollectCoverage | Collect code coverage' },
        [pscustomobject]@{ Key = 'FailFast'; Label = 'FailFast | Stop after the first failed target' },
        [pscustomobject]@{ Key = 'CI'; Label = 'CI | Enable continuous integration output' },
        [pscustomobject]@{ Key = 'FailWarnings'; Label = 'FailWarnings | Treat runner warnings as failures' },
        [pscustomobject]@{ Key = 'NoRestore'; Label = 'NoRestore | Use packages already restored' }
    )
    $selectedFlags = @(Read-SpectreMultiSelection -Message (Get-DevOpsUiPromptTitle 'Optional test flags') -Choices $flagChoices -ChoiceLabelProperty Label -PageSize 10 -AllowEmpty)
    $explicitMode = Read-DevOpsUiSelection -Message 'Explicit test policy' -Choices @('Exclude explicit tests', 'Include explicit tests', 'Run only explicit tests')

    return [pscustomobject]@{
        Scope = $scope; Suites = $suiteKeys; Commit = $commit; Range = $range
        Framework = $framework; Configuration = $configuration; Areas = $areas
        Categories = $categories; Techniques = $techniques; Filter = $runnerFilter
        Count = $countText; Flaky = $isFlaky; Profile = $profile
        HangTimeout = $hangTimeout; Timeout = $processTimeout; Verbosity = $verbosity
        RuntimeIdentifier = $runtimeIdentifier
        Flags = @($selectedFlags | ForEach-Object { $_.Key }); ExplicitMode = $explicitMode
    }
}

function New-DevOpsUiTestArguments {
    param([Parameter(Mandatory = $true)][object]$Options)
    $arguments = [System.Collections.Generic.List[string]]::new()
    [void]$arguments.Add('test')
    [void]$arguments.Add('-Suite'); [void]$arguments.Add(($Options.Suites -join ','))
    if ($Options.Framework) { [void]$arguments.Add('-Framework'); [void]$arguments.Add([string]$Options.Framework) }
    [void]$arguments.Add('-Configuration'); [void]$arguments.Add([string]$Options.Configuration)
    if ($Options.Commit) { [void]$arguments.Add('-Commit'); [void]$arguments.Add([string]$Options.Commit) }
    if ($Options.Range) { [void]$arguments.Add('-Range'); [void]$arguments.Add([string]$Options.Range) }
    if ($Options.Areas.Count -gt 0) { [void]$arguments.Add('-Area'); [void]$arguments.Add(($Options.Areas -join ',')) }
    if ($Options.Categories.Count -gt 0) { [void]$arguments.Add('-Category'); [void]$arguments.Add(($Options.Categories -join ',')) }
    if ($Options.Techniques.Count -gt 0) { [void]$arguments.Add('-Technique'); [void]$arguments.Add(($Options.Techniques -join ',')) }
    if ($Options.Filter) { [void]$arguments.Add('-Filter'); [void]$arguments.Add([string]$Options.Filter) }
    [void]$arguments.Add('-Count'); [void]$arguments.Add([string]$Options.Count)
    [void]$arguments.Add('-Profile'); [void]$arguments.Add([string]$Options.Profile)
    [void]$arguments.Add('-HangTimeout'); [void]$arguments.Add((ConvertTo-DevOpsUiTimeout ([string]$Options.HangTimeout)))
    [void]$arguments.Add('-Timeout'); [void]$arguments.Add((ConvertTo-DevOpsUiTimeout ([string]$Options.Timeout)))
    [void]$arguments.Add('-Verbosity'); [void]$arguments.Add([string]$Options.Verbosity)
    if ($Options.Flaky) { [void]$arguments.Add('-Flaky') }
    if ($Options.RuntimeIdentifier) { [void]$arguments.Add('-RuntimeIdentifier'); [void]$arguments.Add([string]$Options.RuntimeIdentifier) }
    foreach ($name in $Options.Flags) { [void]$arguments.Add("-$name") }
    $explicitMode = $Options.ExplicitMode
    if ($explicitMode -eq 'Include explicit tests') { [void]$arguments.Add('-Explicit') }
    if ($explicitMode -eq 'Run only explicit tests') { [void]$arguments.Add('-ExplicitOnly') }
    return ,$arguments.ToArray()
}

function Get-DevOpsUiRunState {
    param([object[]]$Events)
    $state = [ordered]@{ RunId = ''; RunDirectory = ''; TargetCount = 0; CurrentTarget = ''; Completed = 0; Passed = 0; Failed = 0; Skipped = 0; Recent = [System.Collections.Generic.List[string]]::new(); Affected = $null; Finished = $false; Succeeded = $false }
    foreach ($event in $Events) {
        $data = $event.data
        switch ([string]$event.name) {
            'TargetsResolved' { if ($data.affected) { $state.Affected = $data.affected } }
            'RunStarted' { $state.RunId = [string]$data.runId; $state.RunDirectory = [string]$data.runDirectory; $state.TargetCount = [int]$data.executionCount }
            'TargetsPrepared' { $state.TargetCount = [int]$data.executionCount }
            'TargetStarted' { $state.CurrentTarget = [string]$data.target }
            'TargetProgress' { $state.CurrentTarget = "{0} ({1} elapsed)" -f $data.target, $data.elapsed }
            'TargetCompleted' {
                $state.Completed++
                $state.Passed += [int]$data.passed
                $state.Failed += [int]$data.failed
                $state.Skipped += [int]$data.skipped
                $state.CurrentTarget = "{0} {1}" -f $data.target, $data.outcome
                [void]$state.Recent.Add("$($data.target) $($data.outcome), $($data.testCount) test(s), $([Math]::Round([double]$data.durationMs / 1000, 1))s")
                while ($state.Recent.Count -gt 8) { $state.Recent.RemoveAt(0) }
            }
            'RunWarning' { [void]$state.Recent.Add("WARNING: $($data.message)"); while ($state.Recent.Count -gt 8) { $state.Recent.RemoveAt(0) } }
            'RunError' { [void]$state.Recent.Add("ERROR: $($data.message)"); while ($state.Recent.Count -gt 8) { $state.Recent.RemoveAt(0) } }
            'RunCompleted' { $state.Finished = $true; $state.Succeeded = [bool]$data.succeeded; $state.RunDirectory = [string]$data.runDirectory }
        }
    }
    return [pscustomobject]$state
}

function Invoke-DevOpsUiTestLive {
    param([string[]]$Arguments, [string]$EventPath)
    $initial = New-SpectreLayout -Name 'root' -Rows @(
        (New-SpectreLayout -Name 'summary' -Ratio 1 -Data 'Starting test orchestration...'),
        (New-SpectreLayout -Name 'recent' -Ratio 3 -Data 'Waiting for structured target events...')
    )
    $liveState = [pscustomobject]@{
        Events = [System.Collections.Generic.List[object]]::new()
        Lines = [System.Collections.Generic.List[string]]::new()
        Layout = $initial
        Context = $null
    }
    $processInvoker = Get-Command Invoke-DevOpsUiProcess
    $stateReader = Get-Command Get-DevOpsUiRunState
    $eventReader = {
        param($Event)
        if ($null -ne $Event) { [void]$liveState.Events.Add($Event) }
        $state = & $stateReader -Events $liveState.Events.ToArray()
        $summaryText = "Test run $($state.RunId)`nTargets: $($state.Completed)/$($state.TargetCount) completed`nCurrent: $($state.CurrentTarget)`nTests: $($state.Passed) passed, $($state.Failed) failed, $($state.Skipped) skipped"
        if ($state.Affected) {
            $summaryText += "`nAffected files: $(@($state.Affected.paths).Count) | Targets: $(@($state.Affected.targets) -join ', ')"
        }
        $recentText = if ($state.Recent.Count -gt 0) { $state.Recent -join "`n" } else { 'No target has completed.' }
        if ($liveState.Lines.Count -gt 0) { $recentText += "`n`nRecent output:`n" + (@($liveState.Lines | Select-Object -Last 4) -join "`n") }
        $summaryPanel = [Spectre.Console.Panel]::new([Spectre.Console.Text]::new($summaryText))
        $summaryPanel.Header = [Spectre.Console.PanelHeader]::new('LeanCorpus DevOps | Test progress')
        $summaryPanel.Border = [Spectre.Console.BoxBorder]::Rounded
        $recentPanel = [Spectre.Console.Panel]::new([Spectre.Console.Text]::new($recentText))
        $recentPanel.Header = [Spectre.Console.PanelHeader]::new('Targets and recent output')
        $recentPanel.Border = [Spectre.Console.BoxBorder]::Rounded
        $liveState.Layout['summary'].Update($summaryPanel) | Out-Null
        $liveState.Layout['recent'].Update($recentPanel) | Out-Null
        $liveState.Context.Refresh()
    }.GetNewClosure()
    $lineReader = {
        param($Stream, $Line)
        [void]$liveState.Lines.Add("$Stream`: $Line")
        while ($liveState.Lines.Count -gt 100) { $liveState.Lines.RemoveAt(0) }
    }.GetNewClosure()
    $live = {
        param([Spectre.Console.LiveDisplayContext]$Context)
        $liveState.Context = $Context
        return & $processInvoker -Arguments $Arguments -EventPath $EventPath -OnLine $lineReader -OnTick $eventReader
    }.GetNewClosure()
    return Invoke-SpectreLive -Data $initial -ScriptBlock $live
}

function Invoke-DevOpsUiRunTests {
    param([switch]$ForceAffected)
    $options = Read-DevOpsUiTestOptions -ForceAffected:$ForceAffected
    if ($null -eq $options) { return }
    $arguments = New-DevOpsUiTestArguments -Options $options
    $script:lastTestArguments = @($arguments)
    $script:lastTestOptions = $options
    $eventPath = Join-Path ([IO.Path]::GetTempPath()) ('leancorpus-test-ui-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
    $env:LEANCORPUS_DEVOPS_UI_EVENTS_FILE = $eventPath
    try {
        if ($options.Scope -eq 'Affected source changes') {
            $explain = [System.Collections.Generic.List[string]]::new()
            foreach ($argument in $arguments) { [void]$explain.Add($argument) }
            [void]$explain.Add('-ExplainAffected')
            $null = Show-DevOpsUiCommandPreview -Arguments @($explain.ToArray())
            $explanation = Invoke-DevOpsUiProcess -Arguments $explain.ToArray() -EventPath $eventPath
            if ($explanation.ExitCode -ne 0) {
                Write-DevOpsUiProcessResult -Result $explanation
                return
            }
            Write-DevOpsUiAffectedExplanation -EventPath $eventPath -FallbackOutput $explanation.StdOut
        }
        $preview = Show-DevOpsUiCommandPreview -Arguments @($arguments)
        if (-not (Read-DevOpsUiSwitch -Message 'Run this test command?')) { return }
        $result = Invoke-DevOpsUiTestLive -Arguments @($arguments) -EventPath $eventPath
        Write-DevOpsUiProcessResult -Result $result
        Show-DevOpsUiTestResult -EventPath $eventPath -ExitCode $result.ExitCode
    } finally {
        Remove-Item Env:LEANCORPUS_DEVOPS_UI_EVENTS_FILE -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $eventPath) { Remove-Item -LiteralPath $eventPath -Force -ErrorAction SilentlyContinue }
    }
}

function Write-DevOpsUiAffectedExplanation {
    param([string]$EventPath, [string[]]$FallbackOutput = @())
    $event = $null
    if (Test-Path -LiteralPath $EventPath) {
        foreach ($line in Get-Content -LiteralPath $EventPath) {
            try { $candidate = ConvertFrom-Json $line -Depth 32; if ($candidate.name -eq 'TargetsResolved') { $event = $candidate; break } } catch { }
        }
    }
    Write-SpectreRule -PassThru -Title 'Affected test selection' | Out-SpectreHost | Out-Host
    if ($null -eq $event -or $null -eq $event.data.affected) {
        foreach ($line in $FallbackOutput) { Write-DevOpsUiMarkup (ConvertTo-DevOpsUiLiteralText $line) }
        return
    }
    $affected = $event.data.affected
    Write-DevOpsUiMarkup (ConvertTo-DevOpsUiLiteralText "Source: $($affected.mode) $($affected.suppliedCommit)$($affected.suppliedRange)")
    $fileNodes = @($affected.paths | ForEach-Object { @{ Value = (ConvertTo-DevOpsUiLiteralText ([string]$_)); Children = @() } })
    $targetNodes = @($affected.targets | ForEach-Object { @{ Value = (ConvertTo-DevOpsUiLiteralText ([string]$_)); Children = @() } })
    $tree = @{ Value = 'Affected test intent'; Children = @(
        @{ Value = 'Changed files'; Children = $fileNodes },
        @{ Value = 'Selected targets'; Children = $targetNodes }
    ) }
    Format-SpectreTree -Data $tree | Out-SpectreHost | Out-Host
}

function Show-DevOpsUiTestResult {
    param([string]$EventPath, [int]$ExitCode)
    $events = [System.Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $EventPath) {
        foreach ($line in Get-Content -LiteralPath $EventPath) {
            try { [void]$events.Add((ConvertFrom-Json $line -Depth 32)) } catch { }
        }
    }
    $state = Get-DevOpsUiRunState -Events $events.ToArray()
    $status = if ($ExitCode -eq 0 -and $state.Succeeded) { 'Passed' } elseif ($ExitCode -eq 130) { 'Cancelled' } else { 'Failed' }
    $colour = if ($status -eq 'Passed') { 'green' } elseif ($status -eq 'Cancelled') { 'yellow' } else { 'red' }
    $summary = "[bold $colour]$status[/] | $($state.Completed)/$($state.TargetCount) targets`n$($state.Passed) passed | $($state.Failed) failed | $($state.Skipped) skipped`n`nRun: $(ConvertTo-DevOpsUiLiteralText $state.RunId)`nArtefacts: $(ConvertTo-DevOpsUiLiteralText $state.RunDirectory)"
    Format-SpectrePanel -Data $summary -Header '[bold cyan]LeanCorpus DevOps | Test result[/]' -Border Rounded | Out-SpectreHost | Out-Host
    $reportPath = if ($state.RunDirectory) { Join-Path $state.RunDirectory 'report.json' } else { '' }
    $report = if ($reportPath -and (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 32
    } else { $null }
    if ($null -ne $report -and @($report.targetResults).Count -gt 0) {
        Show-DevOpsUiSummaryTable -Rows @($report.targetResults | ForEach-Object { [pscustomobject]@{
            Target = $_.targetKey; Outcome = $_.outcome; DurationSeconds = [Math]::Round([double]$_.durationMs / 1000, 2)
            Tests = $_.testCount; Failed = $_.failedTestCount; Iteration = $_.iteration
        } })
    }
    $failures = @(if ($null -ne $report) { $report.tests | Where-Object { [int]$_.failedCount -gt 0 } })
    foreach ($failure in @($failures | Select-Object -First 10)) {
        Write-DevOpsUiMarkup "[red]$(ConvertTo-DevOpsUiLiteralText "$($failure.suite): $($failure.testName) | $($failure.classification)")[/]"
    }
    if ($ExitCode -ne 0) {
        $actions = @('Return', 'Re-run selection with diagnostics', 'Run selection repeatedly', 'Open logs', 'Browse artefacts')
        if ($failures.Count -gt 0) { $actions = @('Re-run selected failures') + $actions }
        $action = [string](Read-DevOpsUiSelection -Message 'After the failed test run' -Choices $actions)
        if ($action -eq 'Re-run selected failures') {
            $failureChoices = @($failures | ForEach-Object { "{0} | {1}" -f $_.suite, $_.testName })
            $selectedFailures = @(Read-DevOpsUiMultiSelection -Message 'Select failing tests to repeat' -Choices $failureChoices -PageSize 14)
            if ($selectedFailures.Count -gt 0) {
                $selectedRows = @($failures | Where-Object { ("{0} | {1}" -f $_.suite, $_.testName) -in $selectedFailures })
                $suiteKeys = @($selectedRows | ForEach-Object { [string]$_.suite } | Sort-Object -Unique)
                $filterParts = @($selectedRows | ForEach-Object { "FullyQualifiedName~$($_.testName)" })
                $rerun = @('test', '-Suite', ($suiteKeys -join ','), '-Configuration', [string]$script:lastTestOptions.Configuration,
                    '-Filter', ('(' + ($filterParts -join '|') + ')'))
                if ($script:lastTestOptions.Framework) { $rerun += @('-Framework', [string]$script:lastTestOptions.Framework) }
                $null = Invoke-DevOpsUiCommand -Arguments $rerun -Title 'Re-running selected failures'
            }
        } elseif ($action -eq 'Re-run selection with diagnostics' -or $action -eq 'Run selection repeatedly') {
            $rerun = [System.Collections.Generic.List[string]]::new()
            foreach ($argument in $script:lastTestArguments) { [void]$rerun.Add($argument) }
            if ($action -eq 'Re-run selection with diagnostics' -and '-Diagnostics' -notin $rerun) { [void]$rerun.Add('-Diagnostics') }
            if ($action -eq 'Run selection repeatedly' -and '-Count' -in $rerun) {
                $countIndex = $rerun.IndexOf('-Count')
                $rerun[$countIndex + 1] = '3'
            }
            $null = Invoke-DevOpsUiCommand -Arguments $rerun.ToArray() -Title 'Re-running selected tests'
        } elseif ($action -eq 'Open logs' -or $action -eq 'Browse artefacts') {
            if ($state.RunDirectory -and (Test-Path -LiteralPath $state.RunDirectory)) {
                $files = @(Get-ChildItem -LiteralPath $state.RunDirectory -File -Recurse | Sort-Object FullName)
                if ($files.Count -gt 0) {
                    $files | ForEach-Object { $_ | Add-Member -NotePropertyName Label -NotePropertyValue ([IO.Path]::GetRelativePath($state.RunDirectory, $_.FullName)) -Force }
                    $selectedFile = Read-DevOpsUiSelection -Message 'Run artefact' -Choices $files `
                        -ChoiceLabelProperty Label -EnableSearch
                    if ($null -ne $selectedFile) {
                        Write-DevOpsUiMarkup (ConvertTo-DevOpsUiLiteralText "Artefact: $($selectedFile.FullName)")
                        if ($action -eq 'Open logs') {
                            foreach ($line in @(Get-Content -LiteralPath $selectedFile.FullName -Tail 120 -ErrorAction SilentlyContinue)) {
                                Write-DevOpsUiMarkup (ConvertTo-DevOpsUiLiteralText ([string]$line))
                            }
                        }
                    }
                }
            }
        }
    }
}

function Invoke-DevOpsUiRunMenu {
    while ($true) {
        $item = [string](Read-DevOpsUiSelection -Message 'Run and validate' -Choices @('Tests', 'Affected tests', 'Coverage', 'Native AOT', 'Benchmarks', 'Remote benchmark', 'Back') -EnableSearch)
        switch ($item) {
            'Tests' { Invoke-DevOpsUiRunTests }
            'Affected tests' { Invoke-DevOpsUiRunTests -ForceAffected }
            'Coverage' { Invoke-DevOpsUiCoverage }
            'Native AOT' { Invoke-DevOpsUiAot }
            'Benchmarks' { Invoke-DevOpsUiBenchmarks }
            'Remote benchmark' { Invoke-DevOpsUiBenchmarkRemote }
            default { return }
        }
    }
}

function Invoke-DevOpsUiCoverage {
    $suiteRegistry = Import-PowerShellDataFile (Join-Path (Get-RepoRoot) 'scripts/devops/config/test-suites.psd1')
    $suites = @($suiteRegistry.Keys | Where-Object { $suiteRegistry[$_].Coverage } | Sort-Object)
    $suite = [string](Read-DevOpsUiSelection -Message 'Coverage suite' -Choices (@('all') + $suites) -EnableSearch)
    if (-not $suite) { return }
    $frameworks = if ($suite -eq 'all') { @('net10.0', 'net11.0') } else { @(Get-DevOpsUiFrameworkChoices -SuiteKeys @($suite)) }
    $framework = [string](Read-DevOpsUiSelection -Message 'Coverage framework' -Choices $frameworks)
    $arguments = @('coverage', '-Suite', $suite, '-Framework', $framework)
    $configuration = [string](Read-DevOpsUiSelection -Message 'Coverage configuration' -Choices @('Release', 'Debug'))
    $arguments += @('-Configuration', $configuration)
    if (Read-DevOpsUiSwitch -Message 'Remove previous coverage outputs before running?') { $arguments += '-Clean' }
    if (Read-DevOpsUiSwitch -Message 'Include performance tests excluded by default?') { $arguments += '-IncludePerformance' }
    if (Read-DevOpsUiSwitch -Message 'Generate an HTML coverage report?') { $arguments += '-GenerateReport' }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments -Title 'Collecting coverage' -Destructive:($arguments -contains '-Clean')
}

function Invoke-DevOpsUiAot {
    $arguments = @('aot')
    $framework = [string](Read-DevOpsUiSelection -Message 'Native AOT target' -Choices @('Both supported frameworks', 'net10.0', 'net11.0'))
    if ($framework -ne 'Both supported frameworks') { $arguments += @('-Framework', $framework) }
    $configuration = [string](Read-DevOpsUiSelection -Message 'Native AOT configuration' -Choices @('Release', 'Debug'))
    $arguments += @('-Configuration', $configuration)
    $runtimeIdentifier = Read-DevOpsUiText -Message 'Native AOT runtime identifier (blank auto-detects)' -AllowEmpty
    if ($runtimeIdentifier) { $arguments += @('-RuntimeIdentifier', $runtimeIdentifier) }
    $filter = Read-DevOpsUiText -Message 'Test name filter (optional)' -AllowEmpty
    if ($filter) { $arguments += @('-Filter', $filter) }
    $flaky = Read-DevOpsUiSwitch -Message 'Enable repeated flaky-test sampling?'
    if ($flaky) { $arguments += '-Flaky' }
    $defaultCount = if ($flaky) { '30' } else { '1' }
    $count = Read-DevOpsUiText -Message 'Sequential run count' -DefaultAnswer $defaultCount
    $arguments += @('-Count', $count)
    foreach ($option in @('Diagnostics', 'FailFast', 'Ci', 'NoRestore')) {
        $message = switch ($option) {
            'Diagnostics' { 'Enable diagnostic capture?' }
            'FailFast' { 'Stop after the first failed target?' }
            'Ci' { 'Use CI test policy?' }
            'NoRestore' { 'Skip restore?' }
        }
        if (Read-DevOpsUiSwitch -Message $message) { $arguments += "-$option" }
    }
    $timeout = Read-DevOpsUiText -Message 'Outer process timeout (use off to disable)' -DefaultAnswer 'off'
    if ($timeout -ne 'off') { $arguments += @('-Timeout', $timeout) }
    $verbosity = [string](Read-DevOpsUiSelection -Message 'Runner output verbosity' -Choices @('quiet', 'minimal', 'normal', 'detailed'))
    $arguments += @('-Verbosity', $verbosity)
    $null = Invoke-DevOpsUiCommand -Arguments $arguments -Title 'Running Native AOT smoke tests'
}
