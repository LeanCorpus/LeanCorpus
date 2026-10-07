$ErrorActionPreference = 'Stop'

if ($IsWindows) {
    $cpuModel = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name).Trim()
    $ramBytes = [string](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
} elseif (Test-Path '/proc/cpuinfo') {
    $cpuLine = Get-Content '/proc/cpuinfo' | Where-Object { $_ -match '^model name\s*:' } | Select-Object -First 1
    $cpuModel = if ($cpuLine) { ($cpuLine -split ':', 2)[1].Trim() } else { 'unknown' }
    $memoryLine = Get-Content '/proc/meminfo' | Where-Object { $_ -match '^MemTotal\s*:' } | Select-Object -First 1
    $ramBytes = if ($memoryLine -match '^(?:MemTotal)\s*:\s*(\d+)\s+kB$') {
        [string]([long]$Matches[1] * 1024)
    } else {
        'unknown'
    }
} else {
    $cpuModel = 'unknown'
    $ramBytes = 'unknown'
}

$sdkVersion = (& dotnet --version).Trim()
$entries = @(
    "SPIKE_CPU_MODEL=$($cpuModel -replace '[\r\n]+', ' ')",
    "SPIKE_RAM_BYTES=$ramBytes",
    "DOTNET_SDK_VERSION=$sdkVersion"
)
foreach ($entry in $entries) {
    $name, $value = $entry -split '=', 2
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
if ($env:GITHUB_ENV) {
    [System.IO.File]::AppendAllText($env:GITHUB_ENV, ($entries -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
}
