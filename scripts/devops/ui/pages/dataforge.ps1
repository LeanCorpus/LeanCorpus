$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-DevOpsUiDataForgeProfiles {
    $result = Invoke-DevOpsUiCaptureCommand -Arguments @('dataforge', 'profiles', '-Json') -Title 'Discovering DataForge profiles'
    if ($null -eq $result -or $result.ExitCode -ne 0) {
        if ($null -ne $result) { Write-DevOpsUiProcessResult -Result $result }
        return @()
    }
    try {
        $profiles = @((ConvertFrom-Json -InputObject ($result.StdOut -join [Environment]::NewLine) -Depth 8))
        foreach ($profile in $profiles) {
            $label = "{0} v{1} | {2} records | {3}" -f $profile.profileId, $profile.profileVersion, $profile.defaultCount, $profile.description
            $profile | Add-Member -NotePropertyName Label -NotePropertyValue $label -Force
        }
        return $profiles
    }
    catch { Write-SpectreHost "[red]Could not read DataForge profile descriptors: $($_.Exception.Message)[/]"; return @() }
}

function Invoke-DevOpsUiDataForge {
    while ($true) {
        $action = [string](Read-SpectreSelection -Message 'DataForge workflow' -Choices @('Profiles', 'Generate dataset', 'Inspect generated dataset', 'Verify generated dataset', 'Reproduce generated dataset', 'Reference dataset', 'Release qualification', 'Back') -EnableSearch)
        switch ($action) {
            'Profiles' {
                $profiles = @(Get-DevOpsUiDataForgeProfiles)
                if ($profiles.Count -gt 0) {
                    Show-DevOpsUiSummaryTable -Rows @($profiles | ForEach-Object { [pscustomobject]@{
                        Profile = $_.profileId; Version = $_.profileVersion; Kind = $_.kind; Seed = $_.defaultSeed; Count = $_.defaultCount; Description = $_.description
                    } })
                }
            }
            'Generate dataset' { Invoke-DevOpsUiDataForgeGenerate }
            'Inspect generated dataset' { Invoke-DevOpsUiDataForgePathAction -Action 'inspect' }
            'Verify generated dataset' { Invoke-DevOpsUiDataForgePathAction -Action 'verify' }
            'Reproduce generated dataset' { Invoke-DevOpsUiDataForgePathAction -Action 'reproduce' }
            'Reference dataset' { Invoke-DevOpsUiDataForgeReference }
            'Release qualification' {
                $args = @('dataforge', 'qualify')
                $reference = Read-DevOpsUiText -Message 'Wikipedia reference directory override (blank uses the repository default)' -AllowEmpty
                if ($reference) { $args += @('-ReferencePath', $reference) }
                if (Read-DevOpsUiSwitch -Message 'Preview the release qualification command matrix without running it?') { $args += '-Dry' }
                $null = Invoke-DevOpsUiCommand -Arguments $args -Title 'Running DataForge release qualification'
            }
            default { return }
        }
    }
}

function Invoke-DevOpsUiDataForgeGenerate {
    $profiles = @(Get-DevOpsUiDataForgeProfiles)
    if ($profiles.Count -eq 0) { return }
    $profile = Read-SpectreSelection -Message 'Generated dataset profile' -Choices $profiles `
        -ChoiceLabelProperty Label -EnableSearch
    if ($null -eq $profile) { return }

    $arguments = [System.Collections.Generic.List[string]]::new()
    [void]$arguments.Add('dataforge'); [void]$arguments.Add('generate')
    [void]$arguments.Add('-Profile'); [void]$arguments.Add([string]$profile.profileId)
    [void]$arguments.Add('-Version'); [void]$arguments.Add([string]$profile.profileVersion)
    [void]$arguments.Add('-Seed'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Deterministic seed' -DefaultAnswer ([string]$profile.defaultSeed)))
    $defaultCount = [int]$profile.defaultCount
    $stressMode = ''
    if ([string]$profile.profileId -eq 'leancorpus-stress') {
        $stressMode = [string](Read-SpectreSelection -Message 'Stress workload kind' -Choices @('search', 'vector', 'hybrid'))
        $arguments.AddRange([string[]]@('-Mode', $stressMode))
        if ($stressMode -eq 'search') { $defaultCount = 100000 }
        else { $defaultCount = 50000 }
    }
    [void]$arguments.Add('-Count'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Record count' -DefaultAnswer ([string]$defaultCount)))

    $profileId = [string]$profile.profileId
    if ($profileId -eq 'leancorpus-vector') {
        [void]$arguments.Add('-Dimension'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Vector dimension' -DefaultAnswer '64'))
        $distribution = [string](Read-SpectreSelection -Message 'Vector distribution' -Choices @('Uniform', 'Clustered', 'DenseNeighbourhood', 'NearDuplicate', 'QuantisationFriendly', 'QuantisationHostile'))
        [void]$arguments.Add('-VectorDistribution'); [void]$arguments.Add($distribution)
        [void]$arguments.Add('-ClusterCount'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Cluster count' -DefaultAnswer '8'))
        [void]$arguments.Add('-QueryCount'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Query count' -DefaultAnswer '1'))
    } elseif ($profileId -eq 'leancorpus-hybrid') {
        [void]$arguments.Add('-Dimension'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Vector dimension' -DefaultAnswer '64'))
    } elseif ($profileId -eq 'leancorpus-stress') {
        switch ($stressMode) {
            'search' {
                [void]$arguments.Add('-VeryLongShareBasisPoints'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Very long document share (basis points)' -DefaultAnswer '500'))
                [void]$arguments.Add('-LongShareBasisPoints'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Long document share (basis points)' -DefaultAnswer '2000'))
                [void]$arguments.Add('-CategoryCardinality'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Category cardinality' -DefaultAnswer '2048'))
                [void]$arguments.Add('-RegionCardinality'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Region cardinality' -DefaultAnswer '256'))
                [void]$arguments.Add('-RareAnchorBasisPoints'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Rare anchor share (basis points)' -DefaultAnswer '2'))
            }
            'vector' {
                [void]$arguments.Add('-Dimension'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Vector dimension' -DefaultAnswer '128'))
                $distribution = [string](Read-SpectreSelection -Message 'Vector distribution' -Choices @('DenseNeighbourhood', 'Uniform', 'Clustered', 'NearDuplicate', 'QuantisationFriendly', 'QuantisationHostile'))
                [void]$arguments.Add('-VectorDistribution'); [void]$arguments.Add($distribution)
                $clusterDefault = [string][Math]::Min(8, [int]$count)
                [void]$arguments.Add('-ClusterCount'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Cluster count' -DefaultAnswer $clusterDefault))
                [void]$arguments.Add('-QueryCount'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Query count' -DefaultAnswer '10'))
            }
            'hybrid' {
                [void]$arguments.Add('-Dimension'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Vector dimension' -DefaultAnswer '64'))
                [void]$arguments.Add('-CategoryCardinality'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Category cardinality' -DefaultAnswer '4096'))
                [void]$arguments.Add('-NarrowFilterBasisPoints'); [void]$arguments.Add((Read-DevOpsUiText -Message 'Narrow filter share (basis points)' -DefaultAnswer '5'))
            }
        }
    }
    if ($profileId -eq 'rowles-text-multilingual') {
        $language = [string](Read-SpectreSelection -Message 'Multilingual sample language' -Choices @('en', 'fr', 'de', 'es', 'it', 'pt', 'nl', 'ru', 'ar', 'zh', 'ja', 'ko'))
        [void]$arguments.Add('-Language'); [void]$arguments.Add($language)
    }
    if ($profileId -eq 'leancorpus-stress' -and (Read-DevOpsUiSwitch -Message 'Allow generation beyond standard stress limits?')) { [void]$arguments.Add('-AllowLarge') }
    $outputPath = Read-DevOpsUiText -Message 'Output path (blank uses DataForge default)' -AllowEmpty
    if ($outputPath) { [void]$arguments.Add('-Output'); [void]$arguments.Add($outputPath) }
    $force = Read-DevOpsUiSwitch -Message 'Overwrite an existing generated dataset if needed?'
    if ($force) { [void]$arguments.Add('-Force') }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments.ToArray() -Title 'Generating DataForge dataset' -Destructive:$force
}

function Invoke-DevOpsUiDataForgePathAction {
    param([Parameter(Mandatory = $true)][ValidateSet('inspect', 'verify', 'reproduce')][string]$Action)
    $datasetPath = Read-DevOpsUiText -Message 'Dataset directory or manifest path'
    $null = Invoke-DevOpsUiCommand -Arguments @('dataforge', $Action, $datasetPath) -Title "$Action DataForge dataset"
}

function Invoke-DevOpsUiDataForgeReference {
    $operation = [string](Read-SpectreSelection -Message 'Frozen reference operation' -Choices @('Download source files', 'Build immutable reference', 'Inspect reference', 'Verify reference', 'Back'))
    if ($operation -eq 'Back' -or -not $operation) { return }
    $subcommand = switch ($operation) { 'Download source files' { 'download' } 'Build immutable reference' { 'build' } 'Inspect reference' { 'inspect' } 'Verify reference' { 'verify' } }
    $arguments = [System.Collections.Generic.List[string]]::new()
    [void]$arguments.Add('dataforge'); [void]$arguments.Add('reference'); [void]$arguments.Add($subcommand)
    if ($subcommand -in @('download', 'build')) {
        $cache = Read-DevOpsUiText -Message 'Wikipedia source cache path (blank uses repository default)' -AllowEmpty
        if ($cache) { [void]$arguments.Add('-Cache'); [void]$arguments.Add($cache) }
    }
    if ($subcommand -in @('build', 'inspect', 'verify')) {
        $reference = Read-DevOpsUiText -Message 'Reference output or input path (blank uses repository default)' -AllowEmpty
        if ($reference) {
            $name = if ($subcommand -eq 'build') { '-Output' } else { '-ReferencePath' }
            [void]$arguments.Add($name); [void]$arguments.Add($reference)
        }
    }
    $force = $false
    if ($subcommand -eq 'download') {
        $force = Read-DevOpsUiSwitch -Message 'Replace existing downloaded source files?'
        if ($force) { [void]$arguments.Add('-Force') }
    }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments.ToArray() -Title "DataForge reference $subcommand" -Destructive:$force
}

function Invoke-DevOpsUiDataMenu {
    while ($true) {
        $item = [string](Read-SpectreSelection -Message 'Data' -Choices @('DataForge', 'Gutenberg benchmark data', 'Migration fixtures for 3.1.1', 'Back') -EnableSearch)
        switch ($item) {
            'DataForge' { Invoke-DevOpsUiDataForge }
            'Gutenberg benchmark data' {
                $books = Read-DevOpsUiText -Message 'Number of books to prepare' -DefaultAnswer '200'
                $null = Invoke-DevOpsUiCommand -Arguments @('data', 'gutenberg', '-BookCount', $books) -Title 'Preparing Gutenberg benchmark data'
            }
            'Migration fixtures for 3.1.1' {
                $source = Read-DevOpsUiText -Message 'NuGet package source' -DefaultAnswer 'https://api.nuget.org/v3/index.json'
                $null = Invoke-DevOpsUiCommand -Arguments @('data', 'migration-fixtures-311', '-PackageSource', $source) `
                    -Title 'Regenerating frozen migration fixtures' -Destructive
            }
            default { return }
        }
    }
}
