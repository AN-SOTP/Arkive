<#
.SYNOPSIS
Compiles Arkive's tweak code (eng/tweaks/src, Haxe) and copies it into Arkive.Core, which ships it inside the build.

.DESCRIPTION
Run this after changing any .hx file and commit the updated src/Arkive.Core/Tweaks/arkive-tweaks.swf with the
sources, so building Arkive itself never needs Haxe. Haxe 4.3 is found on PATH, in -HaxePath, or in the
ARKIVE_HAXE environment variable (the folder that holds haxe.exe and std).
#>
param([string]$HaxePath = $env:ARKIVE_HAXE)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$haxe = if ($HaxePath) { Join-Path $HaxePath 'haxe.exe' } else { (Get-Command haxe -ErrorAction SilentlyContinue).Source }
if (-not $haxe -or -not (Test-Path $haxe)) { throw 'Haxe was not found. Pass -HaxePath or set ARKIVE_HAXE to the Haxe folder.' }
if (-not $env:HAXE_STD_PATH) { $env:HAXE_STD_PATH = Join-Path (Split-Path $haxe) 'std' }

Push-Location $here
try {
    & $haxe build.hxml
    if ($LASTEXITCODE -ne 0) { throw "Haxe failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

$target = Join-Path $here '..\..\src\Arkive.Core\Tweaks\arkive-tweaks.swf'
Copy-Item (Join-Path $here 'bin\arkive-tweaks.swf') $target -Force
Write-Host "Tweak code: $((Get-Item $target).Length) bytes -> $target"
