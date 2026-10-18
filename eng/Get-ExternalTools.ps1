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
        throw "Missing or incompatible $($file.name). Download Arkive-Tools-win-x64.zip from https://github.com/AN-SOTP/Arkive/releases/tag/v0.1.0, then run this script with -Archive PATH."
    }
    Write-Host "Verified $($file.name)"
}

# Tools pinned to a public release download (for example vgmstream for the Sounds tab): fetched when missing,
# checked against the archive hash and each file's hash, and kept in a folder of their own.
foreach ($package in @($manifest.packages)) {
    if (-not $package) { continue }
    $folder = Join-Path $Destination $package.name
    $missing = @($package.files | Where-Object {
        $path = Join-Path $folder $_.name
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $_.sha256
    })
    if ($missing.Count -gt 0) {
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('Arkive-tools-' + [guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $temporary | Out-Null
            $download = Join-Path $temporary 'package.zip'
            Invoke-WebRequest -Uri $package.url -OutFile $download -UseBasicParsing
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $package.sha256) { throw "Download hash mismatch: $($package.url)" }
            Add-Type -AssemblyName System.IO.Compression
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($download)
            try {
                foreach ($file in $package.files) {
                    $entries = @($zip.Entries | Where-Object { $_.FullName -ceq $file.name })
                    if ($entries.Count -ne 1 -or $entries[0].Length -gt 10MB) { throw "Invalid $($package.name) archive entry: $($file.name)" }
                    $candidate = Join-Path $temporary $file.name
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $candidate)
                    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $file.sha256) { throw "Tool hash mismatch: $($package.name)/$($file.name)" }
                }
            } finally { $zip.Dispose() }
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            foreach ($file in $package.files) { Copy-Item -LiteralPath (Join-Path $temporary $file.name) -Destination $folder -Force }
        } finally {
            if ([IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Write-Host "Verified $($package.name) ($(@($package.files).Count) files)"
}
