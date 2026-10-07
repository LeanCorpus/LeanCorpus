param(
    [Parameter(Mandatory = $true)][string]$ExperimentSha,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
if ($ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExperimentSha must be the frozen 40-character commit SHA.' }
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../')).Path
$metadataPath = Join-Path ([System.IO.Path]::GetFullPath($EvidenceRoot)) 'windows-environment-collector.json'
if (-not (Test-Path $metadataPath)) { throw "Windows environment metadata is missing: $metadataPath" }
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($metadata.experiment_sha -ne $ExperimentSha) { throw 'Prepared environment SHA does not match the requested experiment SHA.' }
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExperimentSha) { throw "Checkout SHA '$head' does not match frozen SHA '$ExperimentSha'." }
if (& git -C $repositoryRoot status --porcelain --untracked-files=all) { throw 'The source checkout must remain clean during measurement.' }

$env:GITHUB_WORKSPACE = $repositoryRoot
$env:SPIKE_EXPERIMENT_SHA = $ExperimentSha
$env:SPIKE_GITHUB_JOB = 'not_applicable_local_vm'
$env:SPIKE_WINDOWS_EDITION = [string]$metadata.windows_edition
$env:SPIKE_WINDOWS_BUILD = [string]$metadata.windows_build
$env:SPIKE_CPU_MODEL = [string]$metadata.cpu_model
$env:SPIKE_RAM_BYTES = [string]$metadata.ram_bytes
$env:SPIKE_POWER_MODE = [string]$metadata.power_mode
$env:SPIKE_DEFENDER_STATE = [string]$metadata.defender_state
$env:SPIKE_FILTER_STATE = [string]$metadata.other_filter_driver_or_antivirus_state_if_known
$env:DOTNET_SDK_VERSION = [string]$metadata.dotnet_sdk
$env:SPIKE_HYPERVISOR = [string]$metadata.hypervisor
$env:SPIKE_HOST_OS = [string]$metadata.host_os
$env:SPIKE_HOST_STORAGE = [string]$metadata.host_storage_description
$env:SPIKE_HOST_CACHE_POLICY = [string]$metadata.host_cache_policy_if_known
$env:SPIKE_GUEST_CACHE_POLICY = [string]$metadata.guest_write_cache_policy_if_known
$env:SPIKE_VIRTUAL_DISK_TYPE = [string]$metadata.virtual_disk_type
$env:SPIKE_VIRTUAL_CONTROLLER = [string]$metadata.virtual_controller
$env:SPIKE_DISK_BUS = [string]$metadata.disk_bus
$env:SPIKE_LIBVIRT_CACHE_MODE = [string]$metadata.libvirt_cache_mode
$env:SPIKE_LIBVIRT_IO_MODE = [string]$metadata.libvirt_io_mode
$env:SPIKE_DISCARD_MODE = [string]$metadata.discard_mode
$env:SPIKE_DETECT_ZEROES_MODE = [string]$metadata.detect_zeroes_mode
$env:SPIKE_BACKING_STORE_TYPE = [string]$metadata.backing_store_type
$env:SPIKE_BACKING_STORE = [string]$metadata.backing_store_path_or_identifier
$env:SPIKE_HOST_FILESYSTEM = [string]$metadata.host_filesystem_for_vm_image
$env:SPIKE_RESET_METHOD = [string]$metadata.reset_method
$env:SPIKE_SNAPSHOT_ID = [string]$metadata.snapshot_id
