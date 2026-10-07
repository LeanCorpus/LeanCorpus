param(
    [Parameter(Mandatory = $true)][string]$ExpectedExperimentSha,
    [Parameter(Mandatory = $true)][string]$RecoveryDataset
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This preflight must run inside the Windows guest.' }
if ($ExpectedExperimentSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExpectedExperimentSha must be a 40-character commit SHA.' }

$operatingSystem = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$volume = Get-Volume -DriveLetter D
if ($volume.FileSystem -ne 'NTFS') { throw "The disposable index volume must be NTFS; found '$($volume.FileSystem)'." }
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'The required .NET SDK is unavailable.' }
$checkout = 'C:\source\leancorpus'
if (-not (Test-Path (Join-Path $checkout '.git'))) { throw "The expected LeanCorpus checkout is missing: $checkout" }
$head = (& git -C $checkout rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedExperimentSha) {
    throw "Guest checkout SHA '$head' does not equal experiment SHA '$ExpectedExperimentSha'."
}
$dirty = & git -C $checkout status --porcelain
if ($dirty) { throw 'Guest checkout is dirty.' }
$assembly = Join-Path $checkout 'artifacts/bin/Rowles.LeanCorpus.WindowsDurabilitySpike/release_net10.0/Rowles.LeanCorpus.WindowsDurabilitySpike.dll'
if (-not (Test-Path $assembly)) { throw "The frozen Release assembly is missing: $assembly" }
if (-not (Test-Path $RecoveryDataset)) { throw "The frozen recovery dataset is missing: $RecoveryDataset" }
$dataset = Get-Content -LiteralPath $RecoveryDataset -Raw | ConvertFrom-Json
$records = @($dataset.Records)
if ($dataset.Seed -ne 42 -or $dataset.FirstOrdinal -ne 90000 -or $dataset.LastOrdinal -ne 99999 -or
    $records.Count -ne 10000 -or $records[0].Ordinal -ne 90000 -or $records[-1].Ordinal -ne 99999 -or
    $dataset.DatasetIdentity.RecordCount -ne 100000 -or $dataset.DatasetIdentity.ProfileId -ne 'leancorpus-search') {
    throw 'Recovery dataset does not match the frozen DataForge measured-range contract.'
}
$allocation = try {
    $fsInfo = fsutil fsinfo ntfsinfo D: | Out-String
    if ($fsInfo -match 'Bytes Per Cluster:\s+(\d+)') { [long]$Matches[1] } else { 'unknown' }
} catch { 'unknown' }

[pscustomobject]@{
    experiment_sha = $ExpectedExperimentSha
    guest_checkout = $checkout
    guest_checkout_sha = $head
    guest_checkout_clean = $true
    spike_assembly = $assembly
    recovery_dataset = $RecoveryDataset
    windows_edition = $operatingSystem.Caption.Trim()
    windows_build = $operatingSystem.BuildNumber
    cpu_model = $cpu.Name.Trim()
    logical_processors = [Environment]::ProcessorCount
    ram_bytes = [long]$computer.TotalPhysicalMemory
    dotnet_sdk = $sdk
    dotnet_runtimes = @(& dotnet --list-runtimes)
    index_volume = 'D:'
    filesystem = $volume.FileSystem
    volume_serial = (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='D:'").VolumeSerialNumber
    allocation_unit_bytes = $allocation
    powershell = $PSVersionTable.PSVersion.ToString()
} | ConvertTo-Json -Depth 5
