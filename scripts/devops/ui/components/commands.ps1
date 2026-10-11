$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-DevOpsUiShellPath {
    $command = Get-Command pwsh -ErrorAction Stop
    return $command.Source
}

function New-DevOpsUiCommandPreview {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    return ConvertTo-CommandLineText -Command './devops' -Arguments $Arguments
}

function Show-DevOpsUiCommandPreview {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $command = New-DevOpsUiCommandPreview -Arguments $Arguments
    Write-SpectreRule -Title 'Command preview' | Out-SpectreHost
    Write-SpectreHost (Get-SpectreEscapedText $command)
    Write-Host ''
    return $command
}

function Read-DevOpsUiText {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$DefaultAnswer = '',
        [switch]$AllowEmpty
    )

    if ($AllowEmpty) {
        return [string](Read-SpectreText -Message $Message -DefaultAnswer $DefaultAnswer -AllowEmpty)
    }
    return [string](Read-SpectreText -Message $Message -DefaultAnswer $DefaultAnswer)
}

function Read-DevOpsUiSwitch {
    param([Parameter(Mandatory = $true)][string]$Message)
    return [bool](Read-SpectreConfirm -Message $Message)
}

function ConvertFrom-DevOpsUiArgumentText {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $arguments = [System.Collections.Generic.List[string]]::new()
    $value = [System.Text.StringBuilder]::new()
    $quote = [char]0
    $started = $false
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($quote -eq [char]0 -and [char]::IsWhiteSpace($character)) {
            if ($started) {
                [void]$arguments.Add($value.ToString())
                [void]$value.Clear()
                $started = $false
            }
            continue
        }
        if ($character -eq '\' -and $quote -eq '"' -and $index + 1 -lt $Text.Length -and $Text[$index + 1] -eq '"') {
            [void]$value.Append('"')
            $index++
            $started = $true
            continue
        }
        if ($quote -ne [char]0) {
            if ($character -eq $quote) { $quote = [char]0 }
            else { [void]$value.Append($character) }
            $started = $true
            continue
        }
        if ($character -eq '"' -or $character -eq "'") {
            $quote = $character
            $started = $true
            continue
        }
        [void]$value.Append($character)
        $started = $true
    }
    if ($quote -ne [char]0) { throw 'Argument text contains an unmatched quote.' }
    if ($started) { [void]$arguments.Add($value.ToString()) }
    return $arguments.ToArray()
}

function Read-DevOpsUiMultiSelection {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][string[]]$Choices,
        [int]$PageSize = 12
    )

    if ($Choices.Count -eq 0) { return @() }
    return @(Read-SpectreMultiSelection -Message $Message -Choices $Choices -PageSize $PageSize -AllowEmpty)
}

function ConvertTo-DevOpsUiDisplayArguments {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in $Arguments) {
        $value = [string]$argument
        if ($value -match '[\s"]') {
            [void]$result.Add('"' + $value.Replace('"', '\"') + '"')
        } else {
            [void]$result.Add($value)
        }
    }
    return ,$result.ToArray()
}

function Start-DevOpsUiProcess {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $repoRoot = Get-RepoRoot
    $entryPoint = Join-Path $repoRoot 'devops.ps1'
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Get-DevOpsUiShellPath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void]$startInfo.ArgumentList.Add('-NoProfile')
    [void]$startInfo.ArgumentList.Add('-File')
    [void]$startInfo.ArgumentList.Add($entryPoint)
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Could not start the DevOps child process.' }
    return $process
}

function Invoke-DevOpsUiProcess {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [scriptblock]$OnLine,
        [scriptblock]$OnTick,
        [string]$EventPath = ''
    )

    $process = Start-DevOpsUiProcess -Arguments $Arguments
    $stdout = [System.Collections.Generic.List[string]]::new()
    $stderr = [System.Collections.Generic.List[string]]::new()
    $stdoutTask = $process.StandardOutput.ReadLineAsync()
    $stderrTask = $process.StandardError.ReadLineAsync()
    $stdoutDone = $false
    $stderrDone = $false
    $eventLineCount = 0
    try {
        while (-not $process.HasExited -or -not $stdoutDone -or -not $stderrDone) {
            if (-not $stdoutDone -and $stdoutTask.IsCompleted) {
                $line = $stdoutTask.GetAwaiter().GetResult()
                if ($null -eq $line) {
                    $stdoutDone = $true
                } else {
                    [void]$stdout.Add($line)
                    if ($null -ne $OnLine) { & $OnLine -Stream stdout -Line $line }
                    $stdoutTask = $process.StandardOutput.ReadLineAsync()
                }
            }
            if (-not $stderrDone -and $stderrTask.IsCompleted) {
                $line = $stderrTask.GetAwaiter().GetResult()
                if ($null -eq $line) {
                    $stderrDone = $true
                } else {
                    [void]$stderr.Add($line)
                    if ($null -ne $OnLine) { & $OnLine -Stream stderr -Line $line }
                    $stderrTask = $process.StandardError.ReadLineAsync()
                }
            }
            if ($EventPath -and (Test-Path -LiteralPath $EventPath -PathType Leaf)) {
                $events = @(Get-Content -LiteralPath $EventPath -ErrorAction SilentlyContinue)
                while ($eventLineCount -lt $events.Count) {
                    $eventLine = [string]$events[$eventLineCount]
                    $eventLineCount++
                    try {
                        $event = ConvertFrom-Json -InputObject $eventLine -Depth 32 -ErrorAction Stop
                        if ($null -ne $OnTick) { & $OnTick -Event $event }
                    } catch {
                        # A writer may still be appending the final event line.
                        $eventLineCount--
                        break
                    }
                }
            }
            if ($null -ne $OnTick) { & $OnTick -Event $null }
            if (-not $stdoutDone -or -not $stderrDone) { Start-Sleep -Milliseconds 60 }
        }
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout.ToArray()
            StdErr = $stderr.ToArray()
        }
    } catch {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
        throw
    } finally {
        $process.Dispose()
    }
}

function Write-DevOpsUiProcessResult {
    param(
        [Parameter(Mandatory = $true)][object]$Result,
        [int]$TailLines = 40
    )

    $lines = @($Result.StdOut) + @($Result.StdErr)
    if ($lines.Count -gt 0) {
        Write-Host ''
        Write-SpectreRule -Title 'Recent command output' | Out-SpectreHost
        foreach ($line in @($lines | Select-Object -Last $TailLines)) {
            Write-SpectreHost (Get-SpectreEscapedText ([string]$line))
        }
    }
    if ([int]$Result.ExitCode -eq 0) {
        Write-SpectreHost '[green]DevOps command completed successfully.[/]'
    } elseif ([int]$Result.ExitCode -eq 130) {
        Write-SpectreHost '[yellow]DevOps command was cancelled (130).[/]'
    } else {
        Write-SpectreHost "[red]DevOps command failed with exit code $($Result.ExitCode).[/]"
    }
}

function Invoke-DevOpsUiCommand {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$Title = 'Running DevOps command',
        [switch]$Destructive,
        [switch]$Confirmed
    )

    if (-not $Confirmed) {
        $preview = Show-DevOpsUiCommandPreview -Arguments $Arguments
        if (-not (Read-SpectreConfirm -Message "Execute: $preview ?")) { return 130 }
        if ($Destructive -and -not (Read-SpectreConfirm -Message 'This action removes or overwrites data. Confirm explicitly to continue.')) {
            return 130
        }
    } elseif ($Destructive) {
        if (-not (Read-SpectreConfirm -Message 'This action removes or overwrites data. Confirm explicitly to continue.')) { return 130 }
    }

    $action = { Invoke-DevOpsUiProcess -Arguments $Arguments }.GetNewClosure()
    $result = Invoke-SpectreCommandWithStatus -Title $Title -ScriptBlock $action
    Write-DevOpsUiProcessResult -Result $result
    return [int]$result.ExitCode
}

function Invoke-DevOpsUiCaptureCommand {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$Title = 'Running DevOps command'
    )

    $preview = Show-DevOpsUiCommandPreview -Arguments $Arguments
    if (-not (Read-SpectreConfirm -Message "Execute: $preview ?")) { return $null }
    $action = { Invoke-DevOpsUiProcess -Arguments $Arguments }.GetNewClosure()
    return Invoke-SpectreCommandWithStatus -Title $Title -ScriptBlock $action
}

function Read-DevOpsUiEnumValues {
    param(
        [Parameter(Mandatory = $true)][string]$EnumName,
        [string]$SourcePath = 'src/devops/Rowles.LeanCorpus.Tests.Core/Metadata/TestMetadata.cs'
    )

    $path = Join-Path (Get-RepoRoot) $SourcePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Metadata source is missing: $path" }
    $source = Get-Content -LiteralPath $path -Raw
    $match = [regex]::Match($source, "public\s+enum\s+$([regex]::Escape($EnumName))\s*\{(?<body>[^}]*)\}", [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) { throw "Could not discover enum '$EnumName' from $SourcePath." }
    $values = @($match.Groups['body'].Value -split ',' | ForEach-Object {
        $value = ($_ -replace '//.*$', '').Trim()
        if ($value -match '^([A-Za-z_][A-Za-z0-9_]*)') { $Matches[1] }
    } | Where-Object { $_ })
    return @($values)
}

function Get-DevOpsUiFrameworkChoices {
    param([Parameter(Mandatory = $true)][string[]]$SuiteKeys)

    $registry = Import-PowerShellDataFile (Join-Path (Get-RepoRoot) 'scripts/devops/config/test-suites.psd1')
    $common = $null
    foreach ($key in $SuiteKeys) {
        if (-not $registry.ContainsKey($key)) { continue }
        $frameworks = @($registry[$key].Frameworks | ForEach-Object { [string]$_ })
        if ($null -eq $common) { $common = $frameworks } else { $common = @($common | Where-Object { $_ -in $frameworks }) }
    }
    if ($null -eq $common -or $common.Count -eq 0) { throw 'The selected suites have no common supported target framework.' }
    return @($common | Sort-Object)
}

function Get-DevOpsUiProjectFrameworkChoices {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $repoRoot = Get-RepoRoot
    $fullProjectPath = if ([IO.Path]::IsPathRooted($ProjectPath)) {
        [IO.Path]::GetFullPath($ProjectPath)
    } else {
        [IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
    }
    if (-not (Test-Path -LiteralPath $fullProjectPath -PathType Leaf)) {
        throw "Project file does not exist: $ProjectPath"
    }

    $output = @(& dotnet msbuild $fullProjectPath -nologo '-getProperty:TargetFrameworks,TargetFramework' 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Could not evaluate target frameworks for '$ProjectPath'." }
    $text = $output -join [Environment]::NewLine
    $start = $text.IndexOf('{')
    $end = $text.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { throw "MSBuild returned no target framework properties for '$ProjectPath'." }
    $document = ConvertFrom-Json -InputObject $text.Substring($start, $end - $start + 1) -ErrorAction Stop
    $frameworkText = if ($document.Properties.TargetFrameworks) {
        [string]$document.Properties.TargetFrameworks
    } else {
        [string]$document.Properties.TargetFramework
    }
    $frameworks = @($frameworkText -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($frameworks.Count -eq 0) { throw "Project '$ProjectPath' declares no target framework." }
    return $frameworks
}

function Show-DevOpsUiSummaryTable {
    param([Parameter(Mandatory = $true)][object[]]$Rows)
    if ($Rows.Count -gt 0) {
        Format-SpectreTable -Data $Rows | Out-SpectreHost
    }
}
