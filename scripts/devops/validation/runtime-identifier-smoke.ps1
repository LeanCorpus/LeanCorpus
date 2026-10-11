Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$module = Import-Module (Join-Path $PSScriptRoot '../DevOps.psm1') -Force -PassThru
& $module {
    $mappings = @(
        [pscustomobject]@{ OperatingSystem = 'Linux'; Architecture = 'X64'; Expected = 'linux-x64' }
        [pscustomobject]@{ OperatingSystem = 'Linux'; Architecture = 'Arm64'; Expected = 'linux-arm64' }
        [pscustomobject]@{ OperatingSystem = 'macOS'; Architecture = 'X64'; Expected = 'osx-x64' }
        [pscustomobject]@{ OperatingSystem = 'macOS'; Architecture = 'Arm64'; Expected = 'osx-arm64' }
        [pscustomobject]@{ OperatingSystem = 'Windows'; Architecture = 'X64'; Expected = 'win-x64' }
        [pscustomobject]@{ OperatingSystem = 'Windows'; Architecture = 'Arm64'; Expected = 'win-arm64' }
    )

    foreach ($mapping in $mappings) {
        $actual = Get-PlatformRuntimeIdentifier -OperatingSystem $mapping.OperatingSystem -Architecture $mapping.Architecture
        if ($actual -cne $mapping.Expected) {
            throw "RID mapping regression for $($mapping.OperatingSystem)/$($mapping.Architecture): expected '$($mapping.Expected)', got '$actual'."
        }
    }

    foreach ($unsupported in @(
        [pscustomobject]@{ OperatingSystem = 'FreeBSD'; Architecture = 'X64'; ExpectedMessage = 'Unsupported operating system' }
        [pscustomobject]@{ OperatingSystem = 'Linux'; Architecture = 'RiscV64'; ExpectedMessage = 'Unsupported architecture' }
    )) {
        $failure = ''
        try {
            Get-PlatformRuntimeIdentifier -OperatingSystem $unsupported.OperatingSystem -Architecture $unsupported.Architecture | Out-Null
        } catch {
            $failure = $_.Exception.Message
        }
        if (-not $failure.Contains($unsupported.ExpectedMessage, [StringComparison]::Ordinal)) {
            throw "Unsupported RID input $($unsupported.OperatingSystem)/$($unsupported.Architecture) did not fail clearly: '$failure'."
        }
    }

    Write-Host 'Runtime identifier smoke passed: six mappings and two unsupported inputs.'
}
