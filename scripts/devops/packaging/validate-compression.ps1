$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-CompressionArchive {
    param([string]$Path, [string]$PackageId, [string]$Version, [bool]$Symbols = $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $specs = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') })
        if ($specs.Count -ne 1) { throw "Expected one nuspec in $Path" }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($spec.package.metadata.id -cne $PackageId -or $spec.package.metadata.version -cne $Version) {
            throw "Unexpected package identity/version in $Path"
        }
        $groups = @($spec.SelectNodes("//*[local-name()='dependencies']/*[local-name()='group']"))
        if ($PackageId -ne 'LeanCorpus') {
            if ($groups.Count -ne 2) { throw "Expected both framework dependency groups in $Path" }
            $frameworks = @($groups | ForEach-Object { $_.targetFramework } | Sort-Object)
            if (($frameworks -join ',') -cne 'net10.0,net11.0') { throw "Unexpected dependency frameworks in $Path" }
            foreach ($group in $groups) {
                $core = @($group.dependency | Where-Object { $_.id -ceq 'LeanCorpus' })
                if ($core.Count -ne 1 -or ($core[0].version -replace '\s', '') -cne '[4.0.0,5.0.0)') {
                    throw "Incorrect Core dependency range in $Path"
                }
                if (@($group.dependency | Where-Object { $_.id -in @('Rowles.Text', 'Rowles.LeanCorpus') }).Count) {
                    throw "Unexpected direct Text or renamed Core dependency in $Path"
                }
            }
        }
        $assembly = $PackageId.Replace('LeanCorpus', 'Rowles.LeanCorpus')
        $extension = if ($Symbols) { 'pdb' } else { 'dll' }
        foreach ($framework in @('net10.0', 'net11.0')) {
            if (-not $archive.GetEntry("lib/$framework/$assembly.$extension")) {
                throw "Missing $framework $extension asset in $Path"
            }
        }
        if ($Symbols) {
            foreach ($framework in @('net10.0', 'net11.0')) {
                $pdb = [IO.BinaryReader]::new($archive.GetEntry("lib/$framework/$assembly.pdb").Open())
                try {
                    if ([Text.Encoding]::ASCII.GetString($pdb.ReadBytes(4)) -cne 'BSJB') {
                        throw "Expected portable PDB in $Path"
                    }
                } finally { $pdb.Dispose() }
            }
        }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $allowed = $name -in @('[Content_Types].xml', '_rels/.rels', 'README.md', 'LICENCE') -or
                $name -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' -or
                $name -ceq "$PackageId.nuspec"
            if ($Symbols) {
                $allowed = $allowed -or $name -match "^lib/net(10|11)\.0/$([regex]::Escape($assembly))\.pdb$"
            } else {
                $allowed = $allowed -or $name -match "^lib/net(10|11)\.0/$([regex]::Escape($assembly))\.(dll|xml)$"
            }
            if (-not $allowed) { throw "Unexpected package content '$name' in $Path" }
        }
    } finally { $archive.Dispose() }
    Write-Success "Verified $PackageId $Version $(if ($Symbols) { 'symbols' } else { 'package' })"
}

function Test-CompressionPackages {
    param([string]$PackageDirectory, [string]$RepoRoot)
    $run = New-ArtifactRun -Kind test -Target 'compression-package-consumer' -CommandLine './devops pack -CompressionOnly -ValidateCompression' -RepoRoot $RepoRoot
    $status = 'Failed'
    $runRoot = ''
    try {
        foreach ($id in @('LeanCorpus', 'LeanCorpus.Compression.LZ4', 'LeanCorpus.Compression.Snappy', 'LeanCorpus.Compression.Zstandard')) {
            $version = if ($id -eq 'LeanCorpus') { '4.0.0' } else { '2.0.0' }
            Test-CompressionArchive -Path (Join-Path $PackageDirectory "$id.$version.nupkg") -PackageId $id -Version $version
            Test-CompressionArchive -Path (Join-Path $PackageDirectory "$id.$version.snupkg") -PackageId $id -Version $version -Symbols $true
        }

        $runRoot = Join-Path (Get-ArtifactKindRoot -Kind temp -RepoRoot $RepoRoot) ('compression-consumer/' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($runRoot)
        $project = Join-Path $runRoot 'Consumer.csproj'
        $cache = Join-Path $runRoot 'packages'
        # One temporary project per run, reused for each codec/framework. Explicit
        # SDK imports prevent the repository's source inclusion and central versions.
        $projectTemplate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Consumer.csproj.template'))
        $programTemplate = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Program.cs.template'))
        $sources = @(& dotnet nuget list source --format short | ForEach-Object {
            if ($_ -match '^E\s+(.+)$') { $Matches[1].Trim() }
        })
        if ($LASTEXITCODE -ne 0 -or $sources.Count -eq 0) { throw 'Cannot discover configured NuGet feeds.' }
        $config = Join-Path $runRoot 'Consumer.NuGet.Config'
        $local = [Security.SecurityElement]::Escape($PackageDirectory)
        $feeds = for ($i = 0; $i -lt $sources.Count; $i++) {
            $url = [Security.SecurityElement]::Escape($sources[$i])
            "<add key=`"feed$i`" value=`"$url`"/>"
        }
        $mappings = for ($i = 0; $i -lt $sources.Count; $i++) {
            "<packageSource key=`"feed$i`"><package pattern=`"*`"/></packageSource>"
        }
        [IO.File]::WriteAllText($config, "<configuration><packageSources><clear/><add key=`"local`" value=`"$local`"/>$($feeds -join '')</packageSources><packageSourceMapping><packageSource key=`"local`"><package pattern=`"LeanCorpus`"/><package pattern=`"LeanCorpus.Compression.*`"/></packageSource>$($mappings -join '')</packageSourceMapping></configuration>")
        $results = [Collections.Generic.List[object]]::new()
        foreach ($framework in @('net10.0', 'net11.0')) {
            foreach ($codec in @('LZ4', 'Snappy', 'Zstandard')) {
                $registration = if ($codec -eq 'LZ4') { 'Lz4Compression' } else { $codec + 'Compression' }
                $policy = if ($codec -eq 'LZ4') { 'Lz4' } else { $codec }
                $body = $programTemplate.Replace('CODEC', $codec).Replace('REGISTRATION', $registration).Replace('POLICY', $policy)
                [IO.File]::WriteAllText((Join-Path $runRoot 'Program.cs'), $body)
                $content = $projectTemplate.Replace('FRAMEWORK', $framework).Replace('CODEC', $codec)
                [IO.File]::WriteAllText($project, $content.Replace('CORE_VERSION', '[4.0.0]'))
                # Keep configured feeds for third-party packages; the isolated cache
                # and local source provide the newly packed Core/optional versions.
                Invoke-DotNet -Arguments @('restore', $project, '--packages', $cache, '--configfile', $config, '--disable-build-servers', '-p:NuGetAudit=false') -WorkingDirectory $runRoot | ForEach-Object { Write-Host $_ }
                $assets = Get-Content (Join-Path $runRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
                $coreKeys = @($assets.libraries.Keys | Where-Object { $_ -like 'LeanCorpus/*' })
                $optionalKeys = @($assets.libraries.Keys | Where-Object { $_ -like 'LeanCorpus.Compression.*/*' })
                if ($coreKeys.Count -ne 1 -or $coreKeys[0] -cne 'LeanCorpus/4.0.0' -or
                    $optionalKeys.Count -ne 1 -or $optionalKeys[0] -cne "LeanCorpus.Compression.$codec/2.0.0") {
                    throw "Unexpected resolved consumer dependency graph for $codec/$framework"
                }
                foreach ($id in @('LeanCorpus', "LeanCorpus.Compression.$codec")) {
                    $version = if ($id -eq 'LeanCorpus') { '4.0.0' } else { '2.0.0' }
                    $installed = Join-Path $cache "$($id.ToLowerInvariant())/$version/$($id.ToLowerInvariant()).$version.nupkg"
                    $packed = Join-Path $PackageDirectory "$id.$version.nupkg"
                    if ((Get-FileHash $installed).Hash -cne (Get-FileHash $packed).Hash) {
                        throw "Consumer did not install the exact locally packed $id archive."
                    }
                }
                if (@($assets.libraries.Keys | Where-Object { $_ -like 'Rowles.Text/*' }).Count) {
                    throw 'Optional codec consumer must not resolve a direct Rowles.Text dependency.'
                }
                Invoke-DotNet -Arguments @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false') -WorkingDirectory $runRoot | ForEach-Object { Write-Host $_ }
                $dll = Join-Path $runRoot "bin/Release/$framework/Consumer.dll"
                $index = Join-Path $runRoot "index-$codec-$framework"
                Invoke-DotNet -Arguments @($dll, 'write', $index) -WorkingDirectory $runRoot | ForEach-Object { Write-Host $_ }
                Invoke-DotNet -Arguments @($dll, 'read', $index) -WorkingDirectory $runRoot | ForEach-Object { Write-Host $_ }

                # An exact 3.x direct dependency must fail resolution of the optional
                # package's supported-major range, rather than compile by accident.
                [IO.File]::WriteAllText($project, $content.Replace('CORE_VERSION', '[3.1.1]'))
                Push-Location $runRoot
                try {
                    $negativeArgs = @('restore', $project, '--packages', $cache, '--source', $PackageDirectory, '--disable-build-servers', '-p:NuGetAudit=false')
                    foreach ($source in $sources) { $negativeArgs += @('--source', $source) }
                    $negative = & dotnet @negativeArgs 2>&1
                    $negativeExit = $LASTEXITCODE
                } finally { Pop-Location }
                $negative | Set-Content (Join-Path $runRoot "negative-$codec-$framework.log")
                if ($negativeExit -eq 0 -or ($negative -join "`n") -notmatch 'NU1605|NU1107') {
                    throw "Expected Core 3.x dependency conflict for $codec/$framework; see negative log in $runRoot"
                }
                $results.Add([ordered]@{ codec = $codec; framework = $framework; core = '4.0.0'; package = '2.0.0'; roundTrip = 'passed'; rejectsCore3 = $true })
                Write-Success "$codec/$framework clean-consumer round trip and Core 3.x rejection passed"
            }
        }
        $git = Get-ArtifactGitContext -RepoRoot $RepoRoot
        Write-AtomicJsonFile -Path (Join-Path $runRoot 'acceptance.json') -Value ([ordered]@{
            schemaVersion = 1; sourceCommit = $git.commit; generatedAtUtc = [DateTime]::UtcNow.ToString('O'); results = @($results)
        })
        Write-Success "Compression acceptance evidence: $runRoot"
        $status = 'Passed'
    } finally {
        Complete-ArtifactRun -RunDirectory $run.RunDirectory -Status $status -AdditionalValues @{ consumerDirectory = $runRoot }
    }
}
