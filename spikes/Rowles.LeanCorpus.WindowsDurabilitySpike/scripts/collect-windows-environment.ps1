param(
    [Parameter(Mandatory = $true)][string]$ExperimentSha,
    [string]$Hypervisor = 'unknown',
    [string]$HostOs = 'unknown',
    [string]$HostStorage = 'unknown',
    [string]$HostCachePolicy = 'unknown',
    [string]$GuestCachePolicy = 'unknown',
    [string]$VirtualDiskType = 'unknown',
    [string]$VirtualController = 'unknown',
    [string]$DiskBus = 'unknown',
    [string]$LibvirtCacheMode = 'unknown',
    [string]$LibvirtIoMode = 'unknown',
    [string]$DiscardMode = 'unknown',
    [string]$DetectZeroesMode = 'unknown',
    [string]$BackingStoreType = 'unknown',
    [string]$BackingStore = 'unknown',
    [string]$HostFilesystem = 'unknown',
    [string]$ResetMethod = 'unknown',
    [string]$SnapshotId = 'unknown',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required by the Windows evidence collector.' }
if (-not $IsWindows) {
    throw 'This metadata collector runs inside the disposable Windows guest.'
}
if ($ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'ExperimentSha must be the frozen 40-character commit SHA.'
}

$operatingSystem = Get-CimInstance Win32_OperatingSystem
$computerSystem = Get-CimInstance Win32_ComputerSystem
$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$cpuModel = if ($processor.Name) { $processor.Name.Trim() } else { 'unknown' }
$ramBytes = if ($computerSystem.TotalPhysicalMemory) { [string]$computerSystem.TotalPhysicalMemory } else { 'unknown' }
$powerMode = try {
    ((powercfg /getactivescheme | Select-Object -First 1) -join ' ').Trim()
} catch {
    'unknown'
}
$defenderState = try {
    $status = Get-MpComputerStatus
    "realtime_protection=$($status.RealTimeProtectionEnabled);antivirus_enabled=$($status.AntivirusEnabled)"
} catch {
    'unknown'
}
$filterState = try {
    ((fltmc filters | Select-Object -Skip 3) -join '; ').Trim()
} catch {
    'unknown'
}

$env:SPIKE_EXPERIMENT_SHA = $ExperimentSha
$env:SPIKE_WINDOWS_EDITION = if ($operatingSystem.Caption) { $operatingSystem.Caption.Trim() } else { 'unknown' }
$env:SPIKE_WINDOWS_BUILD = if ($operatingSystem.BuildNumber) { [string]$operatingSystem.BuildNumber } else { 'unknown' }
$env:SPIKE_CPU_MODEL = $cpuModel
$env:SPIKE_RAM_BYTES = $ramBytes
$env:SPIKE_POWER_MODE = $powerMode
$env:SPIKE_DEFENDER_STATE = $defenderState
$env:SPIKE_FILTER_STATE = $filterState
$env:DOTNET_SDK_VERSION = (& dotnet --version).Trim()
$env:SPIKE_HYPERVISOR = $Hypervisor
$env:SPIKE_HOST_OS = $HostOs
$env:SPIKE_HOST_STORAGE = $HostStorage
$env:SPIKE_HOST_CACHE_POLICY = $HostCachePolicy
$env:SPIKE_GUEST_CACHE_POLICY = $GuestCachePolicy
$env:SPIKE_VIRTUAL_DISK_TYPE = $VirtualDiskType
$env:SPIKE_VIRTUAL_CONTROLLER = $VirtualController
$env:SPIKE_DISK_BUS = $DiskBus
$env:SPIKE_LIBVIRT_CACHE_MODE = $LibvirtCacheMode
$env:SPIKE_LIBVIRT_IO_MODE = $LibvirtIoMode
$env:SPIKE_DISCARD_MODE = $DiscardMode
$env:SPIKE_DETECT_ZEROES_MODE = $DetectZeroesMode
$env:SPIKE_BACKING_STORE_TYPE = $BackingStoreType
$env:SPIKE_BACKING_STORE = $BackingStore
$env:SPIKE_HOST_FILESYSTEM = $HostFilesystem
$env:SPIKE_RESET_METHOD = $ResetMethod
$env:SPIKE_SNAPSHOT_ID = $SnapshotId

foreach ($name in @(
    'SPIKE_WINDOWS_EDITION', 'SPIKE_WINDOWS_BUILD', 'SPIKE_CPU_MODEL',
    'SPIKE_RAM_BYTES', 'SPIKE_POWER_MODE', 'SPIKE_DEFENDER_STATE', 'SPIKE_FILTER_STATE',
    'DOTNET_SDK_VERSION', 'SPIKE_HYPERVISOR', 'SPIKE_HOST_OS', 'SPIKE_HOST_STORAGE',
    'SPIKE_HOST_CACHE_POLICY', 'SPIKE_GUEST_CACHE_POLICY', 'SPIKE_VIRTUAL_DISK_TYPE',
    'SPIKE_VIRTUAL_CONTROLLER', 'SPIKE_DISK_BUS', 'SPIKE_LIBVIRT_CACHE_MODE',
    'SPIKE_LIBVIRT_IO_MODE', 'SPIKE_DISCARD_MODE', 'SPIKE_DETECT_ZEROES_MODE',
    'SPIKE_BACKING_STORE_TYPE', 'SPIKE_BACKING_STORE', 'SPIKE_HOST_FILESYSTEM',
    'SPIKE_RESET_METHOD', 'SPIKE_SNAPSHOT_ID'
)) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Process'))) {
        [Environment]::SetEnvironmentVariable($name, 'unknown', 'Process')
    }
}

$metadata = [ordered]@{
    experiment_sha = $ExperimentSha
    windows_edition = $env:SPIKE_WINDOWS_EDITION
    windows_build = $env:SPIKE_WINDOWS_BUILD
    cpu_model = $env:SPIKE_CPU_MODEL
    ram_bytes = $env:SPIKE_RAM_BYTES
    dotnet_sdk = $env:DOTNET_SDK_VERSION
    dotnet_runtimes = @(& dotnet --list-runtimes)
    power_mode = $env:SPIKE_POWER_MODE
    defender_state = $env:SPIKE_DEFENDER_STATE
    other_filter_driver_or_antivirus_state_if_known = $env:SPIKE_FILTER_STATE
    hypervisor = $env:SPIKE_HYPERVISOR
    host_os = $env:SPIKE_HOST_OS
    host_storage_description = $env:SPIKE_HOST_STORAGE
    host_cache_policy_if_known = $env:SPIKE_HOST_CACHE_POLICY
    guest_write_cache_policy_if_known = $env:SPIKE_GUEST_CACHE_POLICY
    virtual_disk_type = $env:SPIKE_VIRTUAL_DISK_TYPE
    virtual_controller = $env:SPIKE_VIRTUAL_CONTROLLER
    disk_bus = $env:SPIKE_DISK_BUS
    libvirt_cache_mode = $env:SPIKE_LIBVIRT_CACHE_MODE
    libvirt_io_mode = $env:SPIKE_LIBVIRT_IO_MODE
    discard_mode = $env:SPIKE_DISCARD_MODE
    detect_zeroes_mode = $env:SPIKE_DETECT_ZEROES_MODE
    backing_store_type = $env:SPIKE_BACKING_STORE_TYPE
    backing_store_path_or_identifier = $env:SPIKE_BACKING_STORE
    host_filesystem_for_vm_image = $env:SPIKE_HOST_FILESYSTEM
    reset_method = $env:SPIKE_RESET_METHOD
    snapshot_id = $env:SPIKE_SNAPSHOT_ID
}
$json = $metadata | ConvertTo-Json -Depth 5
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutput = [System.IO.Path]::GetFullPath($OutputPath)
    if (Test-Path $fullOutput) { throw "Environment evidence already exists and will not be replaced: $fullOutput" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $fullOutput) -Force | Out-Null
    [System.IO.File]::WriteAllText($fullOutput, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}
$json
