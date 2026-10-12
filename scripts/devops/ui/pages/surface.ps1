$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DevOpsUiBuildMenu {
    while ($true) {
        $item = [string](Read-DevOpsUiSelection -Message 'Build and distribution' -Choices @('Build', 'Pack NuGet packages', 'Documentation', 'Back') -EnableSearch)
        switch ($item) {
            'Build' { Invoke-DevOpsUiBuild }
            'Pack NuGet packages' { Invoke-DevOpsUiPack }
            'Documentation' { Invoke-DevOpsUiDocs }
            default { return }
        }
    }
}

function Invoke-DevOpsUiBuild {
    $target = [string](Read-DevOpsUiSelection -Message 'Build target' -Choices @('Complete solution', 'Select a project') -EnableSearch)
    $arguments = [System.Collections.Generic.List[string]]::new()
    [void]$arguments.Add('build')
    $configuration = [string](Read-DevOpsUiSelection -Message 'Build configuration' -Choices @('Release', 'Debug'))
    [void]$arguments.Add('-Configuration'); [void]$arguments.Add($configuration)
    if ($target -eq 'Select a project') {
        $repoRoot = Get-RepoRoot
        $projects = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.csproj' -File -Recurse |
            ForEach-Object { [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/') } | Sort-Object)
        $project = [string](Read-DevOpsUiSelection -Message 'Project path' -Choices $projects -EnableSearch -PageSize 16)
        if (-not $project) { return }
        $frameworks = @(Get-DevOpsUiProjectFrameworkChoices -ProjectPath $project)
        [void]$arguments.Add('-Project'); [void]$arguments.Add($project)
        [void]$arguments.Add('-Framework')
        [void]$arguments.Add([string](Read-DevOpsUiSelection -Message 'Project framework' -Choices $frameworks))
    }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments.ToArray() -Title 'Building LeanCorpus'
}

function Invoke-DevOpsUiPack {
    $arguments = [System.Collections.Generic.List[string]]::new()
    [void]$arguments.Add('pack')
    $configuration = [string](Read-DevOpsUiSelection -Message 'Package configuration' -Choices @('Release', 'Debug'))
    [void]$arguments.Add('-Configuration'); [void]$arguments.Add($configuration)
    $packageSet = [string](Read-DevOpsUiSelection -Message 'Shipping package set' -Choices @('All shipping packages', 'Compression packages only'))
    if ($packageSet -eq 'Compression packages only') { [void]$arguments.Add('-CompressionOnly') }
    $validation = if ($packageSet -eq 'Compression packages only') {
        @('None', 'Validate compression packages')
    } else {
        @('None', 'Validate compression packages', 'Validate every shipping package')
    }
    $validationChoice = [string](Read-DevOpsUiSelection -Message 'Package validation' -Choices $validation)
    if ($validationChoice -eq 'Validate compression packages') { [void]$arguments.Add('-ValidateCompression') }
    if ($validationChoice -eq 'Validate every shipping package') { [void]$arguments.Add('-ValidatePackages') }
    if (Read-DevOpsUiSwitch -Message 'Skip build and pack existing outputs?') {
        [void]$arguments.Add('-NoBuild')
    }
    $clean = Read-DevOpsUiSwitch -Message 'Remove previous package output before packing?'
    if ($clean) { [void]$arguments.Add('-Clean') }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments.ToArray() -Title 'Packing NuGet packages' -Destructive:$clean
}

function Invoke-DevOpsUiDocs {
    $subcommand = [string](Read-DevOpsUiSelection -Message 'Documentation action' -Choices @('Build site', 'Generate API metadata', 'Serve documentation', 'Publish benchmark result pages') -EnableSearch)
    $arguments = switch ($subcommand) {
        'Build site' { @('docs', 'build') }
        'Generate API metadata' { @('docs', 'metadata') }
        'Serve documentation' { @('docs', 'serve') }
        'Publish benchmark result pages' { @('benchmarks', 'docs') }
    }
    if ($subcommand -eq 'Build site') {
        if (Read-DevOpsUiSwitch -Message 'Skip benchmark documentation generation?') { $arguments += '-SkipBenchmarks' }
        if (Read-DevOpsUiSwitch -Message 'Skip coverage report generation?') { $arguments += '-SkipCoverage' }
    }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments -Title "Documentation: $subcommand"
}

function Invoke-DevOpsUiServerMenu {
    $configuration = [string](Read-DevOpsUiSelection -Message 'Community Server configuration' -Choices @('Debug', 'Release'))
    $arguments = @('server', 'start', '-Configuration', $configuration)
    if (Read-DevOpsUiSwitch -Message 'Skip build?') { $arguments += '-NoBuild' }
    if (Read-DevOpsUiSwitch -Message 'Skip restore?') { $arguments += '-NoRestore' }
    if (Read-DevOpsUiSwitch -Message 'Listen on all IPv4 interfaces? This exposes an unencrypted local server.') { $arguments += '-External' }
    $passThrough = Read-DevOpsUiText -Message 'Additional application arguments (optional)' -AllowEmpty
    if ($passThrough) { $arguments += @('--') + @(ConvertFrom-DevOpsUiArgumentText -Text $passThrough) }
    $null = Invoke-DevOpsUiCommand -Arguments $arguments -Title 'Starting Community Server'
}

function Invoke-DevOpsUiMaintenanceMenu {
    while ($true) {
        $item = [string](Read-DevOpsUiSelection -Message 'Maintenance' -Choices @('Setup and dependency', 'Clean owned artefacts', 'Back') -EnableSearch)
        switch ($item) {
            'Setup and dependency' {
                if (Read-DevOpsUiSwitch -Message "Install PwshSpectreConsole $(Get-DevOpsUiDependencyVersion) for the interactive UI?") {
                    $null = Invoke-DevOpsUiCommand -Arguments @('setup', '-InstallUi') -Title 'Installing the pinned UI dependency'
                } else {
                    $null = Invoke-DevOpsUiCommand -Arguments @('setup') -Title 'Checking the development environment'
                }
            }
            'Clean owned artefacts' {
                $target = [string](Read-DevOpsUiSelection -Message 'Artefact clean target' -Choices @('default', 'build', 'test', 'coverage', 'docs', 'diagnostics', 'benchmark', 'package', 'cache', 'all'))
                $null = Invoke-DevOpsUiCommand -Arguments @('clean', $target) -Title "Cleaning $target artefacts" -Destructive
            }
            default { return }
        }
    }
}
