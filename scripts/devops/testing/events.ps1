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
