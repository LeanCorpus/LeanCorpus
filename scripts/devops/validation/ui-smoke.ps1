Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$module = Import-Module (Join-Path $PSScriptRoot '../DevOps.psm1') -Force -PassThru
try {
    & $module {
        Import-DevOpsUiDependency
        $script:uiSmokeDirectory = Join-Path ([IO.Path]::GetTempPath()) ('leancorpus-ui-smoke-' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($script:uiSmokeDirectory)
        $script:uiSmokeChildPath = Join-Path $script:uiSmokeDirectory 'child.ps1'
        $eventPath = Join-Path $script:uiSmokeDirectory 'events.jsonl'
        $script:uiSmokeChecks = 0
        function Assert-Ui($Condition, [string]$Message) {
            if (-not $Condition) { throw "UI regression: $Message" }
            $script:uiSmokeChecks++
        }
        @'
param([string]$Mode)
if ($Mode -eq 'dataforge') {
    Write-Output 'Running DataForge [profiles]'
    Write-Output '[{"profileId":"example","profileVersion":1,"kind":"Generated","defaultSeed":42,"defaultCount":8,"description":"Brackets [are] data"}]'
    exit 0
}
Write-Output 'stdout [not markup]'
Write-Output ''
[Console]::Error.WriteLine('stderr [also not markup]')
if ($env:LEANCORPUS_DEVOPS_UI_EVENTS_FILE) {
    @(
        @{ name='RunStarted'; data=@{ runId='run[1]'; runDirectory='path[1]'; executionCount=1 } },
        @{ name='TargetStarted'; data=@{ target='target[1]' } },
        @{ name='RunCompleted'; data=@{ succeeded=($Mode -eq '0'); runDirectory='path[1]' } }
    ) | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 8 } | Set-Content $env:LEANCORPUS_DEVOPS_UI_EVENTS_FILE
}
exit ([int]$Mode)
'@ | Set-Content -LiteralPath $script:uiSmokeChildPath

        # Replace only process creation; the real callback, stream and rendering paths run.
        function script:Start-DevOpsUiProcess {
            param([string[]]$Arguments)
            $info = [Diagnostics.ProcessStartInfo]::new()
            $info.FileName = (Get-Command pwsh).Source
            $info.UseShellExecute = $false
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($value in @('-NoProfile', '-File', $script:uiSmokeChildPath, $Arguments[0])) { [void]$info.ArgumentList.Add($value) }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            [void]$process.Start()
            return $process
        }
        function script:Read-DevOpsUiSwitch { param([string]$Message) return $true }
        function script:Read-DevOpsUiSelection { param([string]$Message, [object[]]$Choices) return 'Return' }

        try {
            $preview = @(Show-DevOpsUiCommandPreview -Arguments @('test', '-Filter', 'FullyQualifiedName~Cases[one]'))
            Assert-Ui ($preview.Count -eq 1 -and $preview[0] -is [string] -and -not $preview[0].Contains([char]27)) 'preview returns one plain command, without rendered output'
            Assert-Ui ($preview[0].Contains('Cases[one]')) 'preview preserves literal brackets'
            Assert-Ui ((ConvertTo-DevOpsUiLiteralText '') -eq '') 'empty output can be escaped'
            Assert-Ui ((ConvertTo-DevOpsUiTimeout '120') -eq '120s') 'bare timeout numbers mean seconds'
            Assert-Ui ((ConvertTo-DevOpsUiTimeout '2m') -eq '2m' -and (ConvertTo-DevOpsUiTimeout 'off') -eq 'off') 'timeout units and disabled timeouts are retained'
            foreach ($code in @(0, 7, 130)) {
                $result = @(Invoke-DevOpsUiCommand -Arguments @([string]$code) -Confirmed)
                Assert-Ui ($result.Count -eq 1 -and $result[0] -eq $code) "status callback returns only exit code $code"
            }
            $profiles = @(Get-DevOpsUiDataForgeProfiles)
            Assert-Ui ($profiles.Count -eq 1 -and $profiles[0].profileId -eq 'example') 'profile capture ignores orchestration text and retains descriptors'
            $env:LEANCORPUS_DEVOPS_UI_EVENTS_FILE = $eventPath
            foreach ($code in @(0, 7)) {
                Remove-Item -LiteralPath $eventPath -ErrorAction SilentlyContinue
                $result = @(Invoke-DevOpsUiTestLive -Arguments @([string]$code) -EventPath $eventPath)
                Assert-Ui ($result.Count -eq 1 -and $result[0].ExitCode -eq $code) "live callback retains process result $code"
                Assert-Ui ($result[0].StdOut -contains 'stdout [not markup]' -and $result[0].StdErr -contains 'stderr [also not markup]') 'live capture preserves both streams with literal brackets'
                $display = @(Show-DevOpsUiTestResult -EventPath $eventPath -ExitCode $code)
                Assert-Ui ($display.Count -eq 0) 'result rendering does not leak text into the caller'
            }
            $options = [pscustomobject]@{
                Suites=@('core','dataforge','text'); Framework='net11.0'; Configuration='Release'; Commit=''; Range=''
                Areas=@('Foundation','CodecKit'); Categories=@('Integration','Chaos'); Techniques=@('PropertyBased','StateMachine')
                Filter='Cases[one]'; Count='5'; Profile='integration'; HangTimeout='120'; Timeout='off'; Verbosity='normal'
                Flaky=$true; RuntimeIdentifier=''; Flags=@('Diagnostics','FailFast','NoRestore'); ExplicitMode='Exclude explicit tests'
            }
            $arguments = New-DevOpsUiTestArguments -Options $options
            Assert-Ui ($arguments -contains '-Diagnostics' -and $arguments -contains '-FailFast' -and $arguments -contains '-NoRestore' -and $arguments -notcontains '-CollectCoverage') 'only selected optional flags reach the command'
            Assert-Ui ($arguments -contains 'core,dataforge,text' -and $arguments -contains '5' -and $arguments -contains '-Flaky' -and $arguments -contains '120s') 'multi-suite selection, repetition and timeout units are retained'
            Write-Host "UI smoke passed ($script:uiSmokeChecks checks)."
        } finally {
            Remove-Item Env:LEANCORPUS_DEVOPS_UI_EVENTS_FILE -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $script:uiSmokeDirectory -Recurse -Force
        }
    }
} finally {
    Remove-Module $module.Name -Force -ErrorAction SilentlyContinue
}
