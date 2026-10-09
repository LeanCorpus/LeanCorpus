$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DevOpsPack {
    param([string[]]$Arguments = @())
    try {
        $parsed = ConvertFrom-DevOpsArguments $Arguments
        $repoRoot = Get-RepoRoot
        $configuration = [string]$parsed.Get('Configuration', 'Release')
        $outputDirectory = Join-Path (Get-ArtifactKindRoot -Kind package -RepoRoot $repoRoot) 'release'
        if ($parsed.Has('Clean') -and (Test-Path $outputDirectory)) {
            Remove-OwnedArtifactPath -Path $outputDirectory -ArtifactRoot (Get-ArtifactRoot -RepoRoot $repoRoot)
        }
        [void][System.IO.Directory]::CreateDirectory($outputDirectory)

        $projects = if ($parsed.Has('CompressionOnly')) {
            @('Rowles.LeanCorpus', 'Rowles.LeanCorpus.Compression.LZ4', 'Rowles.LeanCorpus.Compression.Snappy', 'Rowles.LeanCorpus.Compression.Zstandard') | ForEach-Object {
                Join-Path $repoRoot "src/core/$_/$_.csproj"
            }
        } else { @(Join-Path $repoRoot 'Rowles.LeanCorpus.slnx') }
        Write-Heading 'Packing LeanCorpus packages'
        foreach ($project in $projects) {
            $packArguments = @('pack', $project, '-c', $configuration, '--output', $outputDirectory, '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false', '--tl:off')
            if ($parsed.Has('NoBuild')) { $packArguments += '--no-build' }
            Invoke-DotNet $packArguments | ForEach-Object { Write-Host $_ }
        }

        $git = Get-ArtifactGitContext -RepoRoot $repoRoot
        $packages = @(Get-ChildItem $outputDirectory -File | Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } | Sort-Object Name)
        if ($parsed.Has('CompressionOnly')) {
            $currentNames = @('LeanCorpus.4.0.0', 'LeanCorpus.Compression.LZ4.2.0.0', 'LeanCorpus.Compression.Snappy.2.0.0', 'LeanCorpus.Compression.Zstandard.2.0.0')
            $packages = @($packages | Where-Object {
                [IO.Path]::GetFileNameWithoutExtension($_.Name) -in $currentNames
            })
        }
        $entries = foreach ($package in $packages) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($package.Name)
            $match = [regex]::Match($name, '^(?<id>.+)\.(?<version>\d+\.\d+\.\d+(?:[-+].+)?)$')
            [ordered]@{
                packageId = if ($match.Success) { $match.Groups['id'].Value } else { $name }
                version = if ($match.Success) { $match.Groups['version'].Value } else { '' }
                path = $package.Name
                size = $package.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $package.FullName).Hash.ToLowerInvariant()
            }
        }
        Write-AtomicJsonFile -Path (Join-Path $outputDirectory 'manifest.json') -Value ([ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString('O')
            sourceCommit = $git.commit
            configuration = $configuration
            packages = @($entries)
        })
        if ($parsed.Has('ValidateCompression')) {
            . (Join-Path (Get-ScriptsPath) 'devops/packaging/validate-compression.ps1')
            Test-CompressionPackages -PackageDirectory $outputDirectory -RepoRoot $repoRoot
        }
        Write-Success "Packages written to: $outputDirectory"
        return 0
    } catch {
        Write-Failure "Pack command failed: $($_.Exception.Message)"
        return 1
    }
}
