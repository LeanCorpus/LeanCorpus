$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-CommandLineText {
    param(
        [string]$Command,
        [string[]]$Arguments = @()
    )

    $parts = [System.Collections.Generic.List[string]]::new()
    [void]$parts.Add($Command)
    foreach ($argument in @($Arguments)) {
        $value = [string]$argument
        if ($value -match '[\s"]') {
            [void]$parts.Add('"' + $value.Replace('"', '\"') + '"')
        } else {
            [void]$parts.Add($value)
        }
    }
    return ($parts -join ' ')
}

function ConvertTo-TestCount {
    param(
        [object]$Value,
        [string]$ParameterName = 'count'
    )

    $count = 0
    $parsed = [int]::TryParse(
        [string]$Value,
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$count)
    if (-not $parsed -or $count -lt 1) {
        throw "--$ParameterName must be a positive integer."
    }

    return $count
}

function ConvertTo-ProcessTimeout {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq 'off') {
        return [TimeSpan]::Zero
    }

    $match = [regex]::Match($Value.Trim(), '^(?<number>\d+(?:\.\d+)?)(?<unit>ms|s|m|h|d)$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw "--timeout must use a value such as 30s, 2m, or 500ms, or off."
    }

    $number = [double]::Parse($match.Groups['number'].Value, [Globalization.CultureInfo]::InvariantCulture)
    switch ($match.Groups['unit'].Value.ToLowerInvariant()) {
        'ms' { return [TimeSpan]::FromMilliseconds($number) }
        's'  { return [TimeSpan]::FromSeconds($number) }
        'm'  { return [TimeSpan]::FromMinutes($number) }
        'h'  { return [TimeSpan]::FromHours($number) }
        'd'  { return [TimeSpan]::FromDays($number) }
    }

    throw "Unsupported process timeout unit in '$Value'."
}

function Invoke-TestPipeline {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Targets,
        [Parameter(Mandatory = $true)]
        [object]$Options,
        [Parameter(Mandatory = $true)]
        [string]$CommandLine,
        [string]$DisplayName = 'Test run',
        [string]$RunId = '',
        [string]$RepoRoot = (Get-RepoRoot)
    )

    $context = New-TestRunContext -Options $Options -Targets $Targets `
        -CommandLine $CommandLine -RunId $RunId -RepoRoot $RepoRoot
    Write-TestProgressEvent -Context $context -Name 'RunStarted' -Data @{
        runId = $context.RunId
        runDirectory = $context.RunDirectory
        targetCount = $Targets.Count
        executionCount = $Targets.Count * $Options.Count
    }
    $summary = $null
    $pipelineError = $null
    $reportError = $false

    Write-Heading $DisplayName
    Write-Host "  Targets:       $($Targets.Count)"
    Write-Host "  Iterations:    $($Options.Count)"
    Write-Host "  Configuration: $($Options.Configuration)"
    if ($Options.RequestedFramework) {
        Write-Host "  Framework:     $($Options.RequestedFramework)"
    }
    if ($Options.Flaky) {
        Write-Host '  Mode:          flaky repetition preset'
    }
    if ($Options.Ci) {
        Write-Host '  Mode:          CI prepared-output execution'
    }
    Write-Host ''

    try {
        if ($Options.ResolutionDuration -gt [TimeSpan]::Zero) {
            Add-TestStageTiming -Context $context -Name 'Resolution' -Duration $Options.ResolutionDuration
        }

        $preparation = Prepare-TestTargets -Targets $Targets -Options $Options -RepoRoot $RepoRoot
        Write-TestProgressEvent -Context $context -Name 'TargetsPrepared' -Data @{
            targetCount = @($preparation.Targets).Count
            executionCount = @($preparation.Targets).Count * $Options.Count
            targetKeys = @($preparation.Targets | ForEach-Object { $_.Target.Key })
        }
        foreach ($preparedTarget in @($preparation.Targets)) {
            Write-TestProgressEvent -Context $context -Name 'TargetDiscovered' -Data @{
                target = $preparedTarget.Target.Key
                suite = $preparedTarget.Target.Suite
                areas = @($preparedTarget.Target.Areas)
                framework = $preparedTarget.Target.Framework
            }
        }
        foreach ($preparationTiming in @($preparation.PreparationTimings)) {
            [void]$context.PreparationTimings.Add($preparationTiming)
        }
        if ($preparation.RestoreDuration -gt [TimeSpan]::Zero) {
            Add-TestStageTiming -Context $context -Name 'Restore' -Duration $preparation.RestoreDuration
        }
        if ($preparation.BuildDuration -gt [TimeSpan]::Zero) {
            Add-TestStageTiming -Context $context -Name 'Build' -Duration $preparation.BuildDuration
        }
        if ($preparation.AotPublishDuration -gt [TimeSpan]::Zero) {
            Add-TestStageTiming -Context $context -Name 'AOTPublish' -Duration $preparation.AotPublishDuration
        }

        $executionStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $executionNumber = 0
        $stopScheduling = $false
        for ($iteration = 1; $iteration -le $Options.Count -and -not $stopScheduling; $iteration++) {
            foreach ($preparedTarget in @($preparation.Targets)) {
                if ($stopScheduling) {
                    break
                }

                $executionNumber++
                Write-TestProgressEvent -Context $context -Name 'TargetStarted' -Data @{
                    target = $preparedTarget.Target.Key
                    iteration = $iteration
                    execution = $executionNumber
                    executionCount = $Targets.Count * $Options.Count
                }
                try {
                    $execution = Invoke-TestTarget -PreparedTarget $preparedTarget -Iteration $iteration `
                        -Context $context -ExecutionNumber $executionNumber `
                        -ExecutionCount ($Targets.Count * $Options.Count)
                } catch {
                    $message = "Target '$($preparedTarget.Target.Key)' iteration $iteration failed in the execution infrastructure: $($_.Exception.Message)"
                    [void]$context.InfrastructureErrors.Add($message)
                    Write-TestProgressEvent -Context $context -Name 'RunError' -Data @{ target = $preparedTarget.Target.Key; message = $message }
                    $execution = New-InfrastructureExecutionResult -Target $preparedTarget.Target `
                        -Iteration $iteration -ErrorMessage $message
                    Write-Failure "  [$executionNumber/$($Targets.Count * $Options.Count)] $message"
                    if ($context.ArtifactsEnabled) {
                        try {
                            $errorDirectory = Get-TestTargetArtifactDirectory -Context $context `
                                -Iteration $iteration -Target $preparedTarget.Target
                            Write-AtomicTextFile -Path (Join-Path $errorDirectory 'infrastructure-error.txt') -Content $message
                        } catch {
                            [void]$context.ReportErrors.Add("Could not preserve execution error: $($_.Exception.Message)")
                        }
                    }
                } finally {
                    Clear-TestTargetCheckpoint -Context $context
                }

                [void]$context.ExecutionResults.Add($execution)
                $testResults = @($execution.TestResults)
                Write-TestProgressEvent -Context $context -Name 'TargetCompleted' -Data @{
                    target = $execution.Target.Key
                    iteration = $execution.Iteration
                    outcome = $execution.Outcome
                    durationMs = $execution.DurationMs
                    testCount = $testResults.Count
                    passed = @($testResults | Where-Object Outcome -eq 'Passed').Count
                    failed = @($testResults | Where-Object Outcome -in @('Failed', 'Error', 'Timeout')).Count
                    skipped = @($testResults | Where-Object Outcome -in @('Skipped', 'NotExecuted', 'Inconclusive')).Count
                    artifactDirectory = $execution.ArtifactDirectory
                    trxPath = $execution.TrxPath
                }
                foreach ($coverageError in @(Copy-TestCoverageResults -Context $context -Execution $execution)) {
                    [void]$context.ReportErrors.Add($coverageError)
                    Write-TestProgressEvent -Context $context -Name 'RunWarning' -Data @{ message = $coverageError }
                    Write-Warn "  $coverageError"
                }
                if ($execution.ResultParsingDuration -gt [TimeSpan]::Zero) {
                    $context.ResultParsingDuration = $context.ResultParsingDuration + $execution.ResultParsingDuration
                }
                try {
                    Write-TestRunCheckpoint -Context $context -Status 'Running'
                } catch {
                    [void]$context.ReportErrors.Add("Checkpoint update failed after $($preparedTarget.Target.Key) iteration $($iteration): $($_.Exception.Message)")
                    Write-TestProgressEvent -Context $context -Name 'RunWarning' -Data @{ message = $context.ReportErrors[$context.ReportErrors.Count - 1] }
                    Write-Warn "  Checkpoint update failed: $($_.Exception.Message)"
                }

                if ($Options.FailFast -and $execution.Outcome -ne 'Passed') {
                    $stopScheduling = $true
                    Write-TestProgressEvent -Context $context -Name 'RunWarning' -Data @{ message = 'Fail-fast stopped scheduling new target executions.' }
                    Write-Warn '  Fail-fast stopped scheduling new target executions.'
                }
            }
        }
        $executionStopwatch.Stop()
        if ($executionStopwatch.Elapsed -gt [TimeSpan]::Zero) {
            Add-TestStageTiming -Context $context -Name 'TestExecution' -Duration $executionStopwatch.Elapsed
        }
        if ($context.ResultParsingDuration -gt [TimeSpan]::Zero) {
            Add-TestStageTiming -Context $context -Name 'ResultParsing' -Duration $context.ResultParsingDuration
        }
    } catch {
        $pipelineError = $_.Exception
        [void]$context.InfrastructureErrors.Add("Test pipeline failed: $($_.Exception.Message)")
        Write-TestProgressEvent -Context $context -Name 'RunError' -Data @{ message = "Test pipeline failed: $($_.Exception.Message)" }
        Write-Failure "Test pipeline failed: $($_.Exception.Message)"
    } finally {
        $context.EndTimeUtc = [DateTime]::UtcNow
        try {
            $summary = New-TestRunSummary -Context $context -Targets $Targets `
                -ExecutionResults @($context.ExecutionResults)
            } catch {
                $reportError = $true
                [void]$context.ReportErrors.Add("Summary calculation failed: $($_.Exception.Message)")
                Write-TestProgressEvent -Context $context -Name 'RunError' -Data @{ message = "Summary calculation failed: $($_.Exception.Message)" }
                Write-Failure "Summary calculation failed: $($_.Exception.Message)"
        }

        if ($context.ArtifactsEnabled) {
            if ($null -ne $summary) {
                try {
                    Write-TestRunReports -Context $context -Summary $summary
                } catch {
                    $reportError = $true
                    [void]$context.ReportErrors.Add("Report generation failed: $($_.Exception.Message)")
                    Write-TestProgressEvent -Context $context -Name 'RunError' -Data @{ message = "Report generation failed: $($_.Exception.Message)" }
                    Write-Failure "Report generation failed: $($_.Exception.Message)"
                }
            }
            try {
                $finalStatus = if ($null -eq $pipelineError -and
                    -not $reportError -and
                    $context.ReportErrors.Count -eq 0 -and
                    $null -ne $summary -and
                    [bool]$summary.Succeeded) { 'Passed' } else { 'Failed' }
                Update-TestRunManifest -Context $context -Summary $summary -Status $finalStatus
                Write-TestRunCheckpoint -Context $context -Status $finalStatus
            } catch {
                $reportError = $true
                [void]$context.ReportErrors.Add("Final artefact update failed: $($_.Exception.Message)")
                Write-Failure "Final artefact update failed: $($_.Exception.Message)"
                try {
                    Complete-ArtifactRun -RunDirectory $context.RunDirectory -Status Failed `
                        -AdditionalValues @{ error = $_.Exception.Message }
                } catch {
                    [void]$context.ReportErrors.Add("Failed run finalisation failed: $($_.Exception.Message)")
                }
                try {
                    Write-TestRunCheckpoint -Context $context -Status 'Failed'
                } catch {
                    [void]$context.ReportErrors.Add("Failed checkpoint update failed: $($_.Exception.Message)")
                }
            }
        }
        Write-TestProgressEvent -Context $context -Name 'RunCompleted' -Data @{
            runId = $context.RunId
            runDirectory = $context.RunDirectory
            succeeded = $null -ne $summary -and [bool]$summary.Succeeded -and
                $null -eq $pipelineError -and -not $reportError -and $context.ReportErrors.Count -eq 0
            requestedTargetExecutions = if ($null -ne $summary) { $summary.RequestedTargetExecutions } else { 0 }
            passedTargetExecutions = if ($null -ne $summary) { $summary.PassedTargetExecutions } else { 0 }
            failedTargetExecutions = if ($null -ne $summary) { $summary.FailedTargetExecutions } else { 0 }
            elapsedMs = ($context.EndTimeUtc - $context.StartTimeUtc).TotalMilliseconds
        }
    }

    if ($null -eq $summary) {
        return 1
    }

    Write-Host ''
    Write-Heading 'Test results'
    Write-Host "  Passed:        $($summary.PassedTargetExecutions)/$($summary.RequestedTargetExecutions) target executions"
    Write-Host "  Failed:        $($summary.FailedTargetExecutions)"
    Write-Host "  Crashed:       $($summary.CrashedTargetExecutions)"
    Write-Host "  Hung:          $($summary.HungTargetExecutions)"
    Write-Host "  Incomplete:    $($summary.IncompleteTargetExecutions)"
    Write-Host "  Infrastructure: $($summary.InfrastructureErrorExecutions)"
    Write-Host ("  Failure rate:  {0:P1}" -f $summary.FailureRate)
    if ($summary.FailingIterations.Count -gt 0) {
        Write-Host "  Failed runs:   $($summary.FailingIterations -join ', ')"
    }
    if ($context.ArtifactsEnabled) {
        Write-Host "  Report:        $(Join-Path $context.RunDirectory 'report.md')"
    }

    if ($null -ne $pipelineError -or $reportError -or $context.ReportErrors.Count -gt 0 -or -not $summary.Succeeded) {
        return 1
    }

    return 0
}
