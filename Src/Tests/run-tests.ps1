<#
.SYNOPSIS
    Runs the Jakar.OpenTelemetry test suites and, for unit + integration, enforces the coverage gate.

.EXAMPLE
    ./Src/Tests/run-tests.ps1                       # unit + integration, coverage report and gate
    ./Src/Tests/run-tests.ps1 -Suite unit           # fast: no Docker/PostgreSQL needed
    ./Src/Tests/run-tests.ps1 -Suite automation     # samples + dashboard in a browser
    ./Src/Tests/run-tests.ps1 -Suite appium         # MAUI sample on devices (see Src/Tests/README.md)
    ./Src/Tests/run-tests.ps1 -Suite all -MinimumLineCoverage 99
#>
param(
    [ValidateSet( 'unit', 'integration', 'coverage', 'automation', 'appium', 'all' )]
    [string] $Suite = 'coverage',

    [ValidateSet( 'Debug', 'Release' )]
    [string] $Configuration = 'Debug',

    # Line and branch coverage required across the libraries and the API (samples, Razor markup and generated code are excluded).
    [double] $MinimumLineCoverage   = 99,
    [double] $MinimumBranchCoverage = 95
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path ( Split-Path $PSScriptRoot -Parent ) -Parent # the repository (this script is in Src/Tests)
$results = Join-Path $root 'TestResults'
$report  = Join-Path $root 'coverage'

$unit = @(
    'Src/Tests/Jakar.OpenTelemetry.Contracts.Tests',
    'Src/Tests/Jakar.OpenTelemetry.Api.Tests',
    'Src/Tests/Jakar.OpenTelemetry.Api.Client.Tests',
    'Src/Tests/Jakar.OpenTelemetry.Api.Client.AspNet.Tests',
    'Src/Tests/Jakar.OpenTelemetry.Api.Client.Blazor.Server.Tests',
    'Src/Tests/Jakar.OpenTelemetry.Api.Client.Blazor.WebAssembly.Tests',
    'Src/Tests/Jakar.OpenTelemetry.Api.Client.Maui.Tests'
)
$integration = @( 'Src/Tests/Jakar.OpenTelemetry.Api.IntegrationTests' )
$automation  = @( 'Src/Tests/Jakar.OpenTelemetry.Samples.AutomationTests' )
$appium      = @( 'Src/Tests/Jakar.OpenTelemetry.Samples.Maui.AppiumTests' )

function Invoke-Tests( [string[]] $projects, [switch] $Coverage )
{
    $failed = $false
    foreach ( $project in $projects )
    {
        $arguments = @( 'test', ( Join-Path $root $project ), '-c', $Configuration, '--nologo' )
        if ( $Coverage ) { $arguments += @( '--settings', ( Join-Path $PSScriptRoot 'coverage.runsettings' ), '--collect', 'XPlat Code Coverage', '--results-directory', $results ) }

        & dotnet @arguments | Out-Host # keep test output out of the function result
        if ( $LASTEXITCODE -ne 0 ) { $failed = $true }
    }
    return -not $failed
}

$ok = $true

switch ( $Suite )
{
    'unit'        { $ok = Invoke-Tests $unit }
    'integration' { $ok = Invoke-Tests $integration }
    'automation'  { $ok = Invoke-Tests $automation }
    'appium'      { $ok = Invoke-Tests $appium }
    default
    {
        if ( Test-Path $results ) { Remove-Item $results -Recurse -Force }

        $ok = Invoke-Tests ( $unit + $integration ) -Coverage

        dotnet tool restore | Out-Null
        dotnet reportgenerator "-reports:$results/**/coverage.cobertura.xml" "-targetdir:$report" '-reporttypes:Html;TextSummary;Cobertura' `
            '-assemblyfilters:+Jakar.OpenTelemetry.Contracts;+Jakar.OpenTelemetry.Api;+Jakar.OpenTelemetry.Api.Client*' | Out-Null

        Get-Content ( Join-Path $report 'Summary.txt' ) | Select-Object -First 20

        [xml] $cobertura = Get-Content ( Join-Path $report 'Cobertura.xml' )
        $line   = [math]::Round( [double] $cobertura.coverage.'line-rate'   * 100, 2 )
        $branch = [math]::Round( [double] $cobertura.coverage.'branch-rate' * 100, 2 )

        if ( $line -lt $MinimumLineCoverage )     { Write-Error "Line coverage $line% is below $MinimumLineCoverage%" -ErrorAction Continue; $ok = $false }
        if ( $branch -lt $MinimumBranchCoverage ) { Write-Error "Branch coverage $branch% is below $MinimumBranchCoverage%" -ErrorAction Continue; $ok = $false }
        Write-Host "Coverage: $line% lines, $branch% branches. HTML report: $report/index.html"

        if ( $Suite -eq 'all' )
        {
            $ok = ( Invoke-Tests $automation ) -and $ok
            $ok = ( Invoke-Tests $appium ) -and $ok
        }
    }
}

if ( -not $ok ) { exit 1 }
