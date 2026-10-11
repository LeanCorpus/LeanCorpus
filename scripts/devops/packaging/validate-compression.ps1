$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Retain the narrow compatibility entry point. Shared isolation, restore, archive
# verification and evidence are implemented by the package consumer harness.
. (Join-Path $PSScriptRoot 'validate-package-consumers.ps1')

function Test-CompressionPackages {
    param([string]$PackageDirectory, [string]$RepoRoot)
    Test-PackageConsumerSet -PackageDirectory $PackageDirectory -RepoRoot $RepoRoot -Scope compression
}
