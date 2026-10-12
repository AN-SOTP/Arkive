param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$PlatformToolset
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Arkive.Crunch.Native\Arkive.Crunch.Native.vcxproj"
$output = Join-Path $root "src\Arkive.Crunch.Native\x64\$Configuration\Arkive.Crunch.Native.dll"
$runtime = Join-Path $root "src\Arkive.Core\runtimes\win-x64\native\Arkive.Crunch.Native.dll"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msbuild = if ($installation) { Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe' }
if (-not $msbuild) {
    throw "Visual Studio MSBuild with the C++ desktop toolchain was not found."
}

$arguments = @($project, '/t:Rebuild', "/p:Configuration=$Configuration", '/p:Platform=x64', '/m')
if ($PlatformToolset) { $arguments += "/p:PlatformToolset=$PlatformToolset" }
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Native Crunch build failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $runtime) | Out-Null
Copy-Item -LiteralPath $output -Destination $runtime -Force
$file = Get-Item -LiteralPath $runtime
$hash = Get-FileHash -LiteralPath $runtime -Algorithm SHA256
Write-Host "Runtime: $($file.FullName)"
Write-Host "Bytes:   $($file.Length)"
Write-Host "SHA-256: $($hash.Hash)"
