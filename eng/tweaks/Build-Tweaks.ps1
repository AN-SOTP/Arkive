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

# The SWC is a zip; its library.swf holds one code block per class, which is what Arkive adds to movies.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$target = [System.IO.Path]::GetFullPath((Join-Path $here '..\..\src\Arkive.Core\Tweaks\arkive-tweaks.swf'))
$zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $here 'bin\arkive-tweaks.swc'))
try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('library.swf'), $target, $true) }
finally { $zip.Dispose() }
Write-Host "Tweak code: $((Get-Item $target).Length) bytes -> $target"
