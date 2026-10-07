param(
    [Parameter(Mandatory = $true)][string]$ConfigFile,
    [switch]$WaitForExit
)

$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json
if ($config.ExperimentSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExperimentSha must be a frozen 40-character commit SHA.' }
if (-not $IsWindows) { throw 'This child launcher runs inside the Windows guest.' }

$indexPath = [System.IO.Path]::GetFullPath([string]$config.TrialIndex)
$controlPath = [System.IO.Path]::GetFullPath([string]$config.ControlPath)
if ([System.IO.Path]::GetPathRoot($indexPath) -eq [System.IO.Path]::GetPathRoot($controlPath)) {
    throw 'The hard-reset control record must be stored outside the index volume.'
}
if (Test-Path $indexPath) { throw "Trial index already exists and will not be reused: $indexPath" }
if (Test-Path $controlPath) { throw "Control record already exists and will not be reused: $controlPath" }

$env:SPIKE_EXPERIMENT_SHA = [string]$config.ExperimentSha
foreach ($property in $config.Environment.PSObject.Properties) {
    [Environment]::SetEnvironmentVariable([string]$property.Name, [string]$property.Value, 'Process')
}

$argumentValues = @(
    $config.AssemblyPath,
    'crash-child', '--mode', 'hard-reset', '--candidate', $config.Candidate,
    '--representation', $config.Representation, '--failpoint', $config.Failpoint,
    '--trial-index', $config.TrialIndex, '--control', $config.ControlPath,
    '--trial-id', $config.TrialId, '--dataset', $config.DatasetPath
)
$startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($value in $argumentValues) { $startInfo.ArgumentList.Add([string]$value) }
$process = [System.Diagnostics.Process]::Start($startInfo)
if (-not $process) { throw 'Failed to start the hard-reset child.' }
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$started = ConvertTo-Json -InputObject @{ process_id = $process.Id; trial_id = $config.TrialId; started_utc = [DateTimeOffset]::UtcNow } -Compress
[Console]::Out.WriteLine($started)
[Console]::Out.Flush()
if ($WaitForExit) {
    $process.WaitForExit()
    if ($config.ChildStdoutPath) {
        [System.IO.File]::WriteAllText([string]$config.ChildStdoutPath, $stdoutTask.GetAwaiter().GetResult())
    }
    if ($config.ChildStderrPath) {
        [System.IO.File]::WriteAllText([string]$config.ChildStderrPath, $stderrTask.GetAwaiter().GetResult())
    }
    exit $process.ExitCode
}
