[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Destination,
    [int]$Major = 10
)

# Arkive ships its own .NET runtime so users need not install one. Instead of spreading it across the install
# folder (a self-contained publish puts ~270 runtime files next to Arkive.App.exe), it is copied in the layout of a
# .NET install into one folder, which the app hosts are built to use (AppHostDotNetSearch=AppRelative):
#   runtime\host\fxr\<version>\hostfxr.dll
#   runtime\shared\Microsoft.NETCore.App\<version>\
#   runtime\shared\Microsoft.WindowsDesktop.App\<version>\
# The newest version present for both frameworks in the building machine's .NET install is used. Their language
# resource folders (ko, ja, ...) are kept, for when Arkive is translated.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$listed = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0) { throw 'dotnet --list-runtimes failed.' }
$runtimes = foreach ($line in $listed) {
    if ($line -match '^(Microsoft\.NETCore\.App|Microsoft\.WindowsDesktop\.App) (\d+\.\d+\.\d+) \[(.+)\]$') {
        [pscustomobject]@{ Name = $Matches[1]; Version = [version]$Matches[2]; Root = $Matches[3] }
    }
}
$core = $runtimes | Where-Object { $_.Name -eq 'Microsoft.NETCore.App' -and $_.Version.Major -eq $Major }
$desktop = $runtimes | Where-Object { $_.Name -eq 'Microsoft.WindowsDesktop.App' -and $_.Version.Major -eq $Major }
$version = $core.Version | Where-Object { $desktop.Version -contains $_ } | Sort-Object -Descending | Select-Object -First 1
if (-not $version) { throw "No .NET $Major runtime is installed with both Microsoft.NETCore.App and Microsoft.WindowsDesktop.App." }
$text = $version.ToString()
$dotnetRoot = Split-Path -Parent (Split-Path -Parent (($core | Where-Object Version -eq $version | Select-Object -First 1).Root))
$fxr = Join-Path $dotnetRoot "host\fxr\$text"
if (-not (Test-Path -LiteralPath (Join-Path $fxr 'hostfxr.dll'))) { throw "hostfxr $text is missing from $dotnetRoot." }

$target = Join-Path $Destination 'runtime'
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
foreach ($part in @("host\fxr\$text", "shared\Microsoft.NETCore.App\$text", "shared\Microsoft.WindowsDesktop.App\$text")) {
    $to = Join-Path $target $part
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath (Join-Path $dotnetRoot $part) -Destination $to -Recurse
}
Write-Host "Private .NET runtime $text copied from $dotnetRoot"
return $text
