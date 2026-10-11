$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-CompressionPackageDependencyContract {
    return [ordered]@{
        packageIds = @(
            'LeanCorpus.Compression.LZ4'
            'LeanCorpus.Compression.Snappy'
            'LeanCorpus.Compression.Zstandard'
        )
        coreVersionRange = '[4.0.0,5.0.0)'
    }
}

function Assert-CompressionPackageDependencyRange {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string[]]$ExpectedFrameworks
    )

    $contract = Get-CompressionPackageDependencyContract
    if ($PackageId -notin $contract.packageIds) {
        throw "Package $PackageId is not covered by the compression dependency contract."
    }

    $archiveInfo = Get-PackageArchiveInfo -Path $PackagePath
    if ($archiveInfo.id -cne $PackageId) {
        throw "Compression package archive ID '$($archiveInfo.id)' does not match $PackageId in $PackagePath."
    }

    $expected = @($ExpectedFrameworks | ForEach-Object { ([string]$_).ToLowerInvariant() } | Sort-Object -Unique)
    if ($expected.Count -eq 0) {
        throw "Compression package $PackageId has no expected dependency frameworks."
    }

    $groups = @($archiveInfo.dependencies)
    if ($groups.Count -ne $expected.Count) {
        throw "Compression package $PackageId must declare dependency groups for '$($expected -join ',')'; found $($groups.Count) groups in $PackagePath."
    }

    foreach ($framework in $expected) {
        $frameworkGroups = @($groups | Where-Object {
            (ConvertTo-PackageFrameworkName ([string]$_.targetFramework)) -ceq $framework
        })
        if ($frameworkGroups.Count -ne 1) {
            throw "Compression package $PackageId must declare exactly one '$framework' dependency group in $PackagePath."
        }

        $coreDependencies = @($frameworkGroups[0].dependencies | Where-Object { $_.id -ceq 'LeanCorpus' })
        if ($coreDependencies.Count -ne 1 -or
            ($coreDependencies[0].version -replace '\s', '') -cne $contract.coreVersionRange) {
            throw "Compression package $PackageId must declare LeanCorpus $($contract.coreVersionRange) in its '$framework' dependency group."
        }
    }
}
