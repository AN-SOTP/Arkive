[CmdletBinding()]
param([string]$Archive, [string]$Destination)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $root 'tools\external' }
$manifest = Get-Content (Join-Path $PSScriptRoot 'ExternalTools.json') -Raw | ConvertFrom-Json
if ($Archive) {
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('Arkive-tools-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporary | Out-Null
        Add-Type -AssemblyName System.IO.Compression
        $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
        try {
            New-Item -ItemType Directory -Force -Path $Destination | Out-Null
            foreach ($file in $manifest.files) {
                $entries = @($zip.Entries | Where-Object { $_.FullName -ceq $file.name })
                if ($entries.Count -ne 1 -or $entries[0].Length -gt 10MB) { throw "Invalid tool archive entry: $($file.name)" }
                $candidate = Join-Path $temporary $file.name
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $candidate)
                if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $file.sha256) { throw "Tool hash mismatch: $($file.name)" }
            }
            foreach ($file in $manifest.files) { Copy-Item -LiteralPath (Join-Path $temporary $file.name) -Destination $Destination -Force }
        } finally { $zip.Dispose() }
    } finally {
        if ($temporary -and [IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $temporary -Recurse -Force
        }
    }
}
foreach ($file in $manifest.files) {
    $path = Join-Path $Destination $file.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Missing or incompatible $($file.name). Download Arkive-Tools-win-x64.zip from the private v0.1.0 release, then run this script with -Archive PATH."
    }
    Write-Host "Verified $($file.name)"
}
