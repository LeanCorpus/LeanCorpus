Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$module = Import-Module (Join-Path $PSScriptRoot '../DevOps.psm1') -Force -PassThru
try {
    & $module {
        $script:checks = 0
        function Assert-Selection($Condition, $Message) {
            if (-not $Condition) { throw "Test selection regression: $Message" }
            $script:checks++
        }
        function Assert-Fails([scriptblock]$Action, [string]$Message) {
            $failure = ''
            try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
            Assert-Selection ($failure.Contains($Message)) "Expected '$Message', got '$failure'."
        }

        $filter = Build-TestFilter -Area 'Index,Search' -Category 'Unit,Chaos' `
            -Technique 'PropertyBased,Metamorphic' -Filter 'FullyQualifiedName~Writer'
        Assert-Selection ($filter -ceq '(Area=Index|Area=Search)&(Category=Unit|Category=Chaos)&(Technique=PropertyBased|Technique=Metamorphic)&FullyQualifiedName~Writer') `
            'trait dimensions are ORed internally and ANDed across dimensions'

        $suites = @{
            core = @{ Name = 'Core'; Project = 'core.csproj'; Frameworks = @('net10.0', 'net11.0') }
            text = @{ Name = 'Text'; Project = 'text.csproj'; Frameworks = @('net10.0', 'net11.0') }
        }
        $targets = @(Resolve-TestTargets -Suite @('core', 'text') -Framework 'net10.0' `
            -FrameworkExplicit $true -Area 'Index,Search' -Category 'Unit,Chaos' `
            -Technique 'PropertyBased,Metamorphic' -Filter 'FullyQualifiedName~Writer' -TestSuites $suites)
        Assert-Selection ((@($targets | ForEach-Object { $_.Suite }) -join ',') -ceq 'core,text') `
            'explicit suites produce one ordered target each'
        Assert-Selection (@($targets | Where-Object { $_.Filter -ceq $filter }).Count -eq 2) `
            'the same composed filter reaches every selected suite'
        Assert-Selection ((@($targets[0].Techniques) -join ',') -ceq 'PropertyBased,Metamorphic') `
            'target metadata retains the selected techniques'
        Assert-Fails { Resolve-TestTargets -Suite @('all', 'core') -TestSuites $suites } `
            "cannot be combined with explicit suites"
        Assert-Fails { Resolve-TestTargets -Suite @('affected', 'core') -TestSuites $suites } `
            "cannot be combined with explicit suites"

        Assert-Selection ($null -eq (Get-Module -Name PwshSpectreConsole)) `
            'loading the normal DevOps module does not import the optional UI dependency'
        Write-Host "Test selection smoke passed ($script:checks checks)."
    }
} finally {
    Remove-Module $module.Name -Force -ErrorAction SilentlyContinue
}
