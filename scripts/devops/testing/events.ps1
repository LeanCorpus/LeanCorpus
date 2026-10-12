$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-DevOpsUiTestEvent {
    param(
        [string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [hashtable]$Data = @{}
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    try {
        $event = [ordered]@{
            schemaVersion = 1
            name = $Name
            atUtc = [DateTime]::UtcNow.ToString('O')
            data = $Data
        }
        $json = ConvertTo-Json -InputObject $event -Depth 16 -Compress
        [System.IO.File]::AppendAllText(
            $Path,
            $json + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))
    } catch {
        # UI telemetry is observational and must never change the test result.
    }
}

function Write-TestProgressEvent {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][string]$Name,
        [hashtable]$Data = @{}
    )

    if ($Context.Options.PSObject.Properties['UiEventPath']) {
        Write-DevOpsUiTestEvent -Path ([string]$Context.Options.UiEventPath) `
            -Name $Name -Data $Data
    }
}

function New-TestProgressCallback {
    param(
        [Parameter(Mandatory = $true)][object]$Context,
        [Parameter(Mandatory = $true)][object]$Target,
        [int]$Iteration,
        [int]$ExecutionNumber,
        [int]$ExecutionCount
    )

    # GetNewClosure creates a dynamic module. Keep the event writer bound to
    # its owning module so its private helpers remain available to the callback.
    $eventWriter = Get-Command Write-TestProgressEvent -CommandType Function
    return {
        param($process, $elapsed)
        $elapsedText = ([TimeSpan]$elapsed).ToString('hh\:mm\:ss')
        & $eventWriter -Context $Context -Name 'TargetProgress' -Data @{
            target = $Target.Key
            iteration = $Iteration
            execution = $ExecutionNumber
            elapsed = $elapsedText
            elapsedMs = ([TimeSpan]$elapsed).TotalMilliseconds
        }
        Write-Host "  [$ExecutionNumber/$ExecutionCount] $($Target.Key) still running ($elapsedText elapsed)..." -ForegroundColor DarkGray
    }.GetNewClosure()
}
