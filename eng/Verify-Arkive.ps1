[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RequireExternalCrunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Arkive.slnx'
$nugetConfig = Join-Path $root 'NuGet.Config'
$crunch = Join-Path $root 'tools\external\crunch_x64.exe'

if ($RequireExternalCrunch -and -not (Test-Path -LiteralPath $crunch -PathType Leaf)) {
    throw "The pinned Crunch encoder is required but was not found at $crunch"
}

Push-Location $root
try {
    & dotnet restore $solution --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    & dotnet build $solution -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    & dotnet run --project 'tests/Arkive.SmokeTests/Arkive.SmokeTests.csproj' `
        -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Arkive smoke tests failed.' }

    & dotnet run --project 'tests/Arkive.UiSmokeTests/Arkive.UiSmokeTests.csproj' `
        -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Arkive UI smoke tests failed.' }
}
finally {
    Pop-Location
}
