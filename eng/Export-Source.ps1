[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination must not already exist; source snapshots are never overwritten.' }
$files = @('.editorconfig', '.gitignore', '.gitattributes', 'Arkive.slnx', 'Directory.Build.props', 'NuGet.Config',
    'global.json', 'README.md', 'LICENSE.txt', 'COPYRIGHT.txt', 'THIRD-PARTY-NOTICES.txt', 'lpk_extract_v5.bms',
    'tools/Build-CrunchNative.ps1', 'tools/decrypt_lost_ark_databases.py', 'tools/build_game_asset_metadata.py',
    'tools/lpk_dump_encrypted_databases.bms')
$files += @('docs/screenshots/asset-library.png', 'docs/screenshots/edit-mods.png', 'docs/screenshots/mod-library.png',
    'docs/screenshots/model-viewer-thaemine.png', 'docs/screenshots/model-viewer-kazeros.png', 'docs/screenshots/model-viewer-edits.png',
    'src/Arkive.App/Viewer/vendor/three/LICENSE')
$extensions = @('.cs', '.xaml', '.csproj', '.vcxproj', '.h', '.cpp', '.txt', '.ico', '.jpg', '.ps1', '.json', '.iss', '.yml',
    '.html', '.js', '.css')
foreach ($directory in @('src', 'tests', 'eng', 'installer', '.github')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $directory) -File -Recurse |
        Where-Object { $_.Extension -in $extensions -and $_.FullName -notmatch '[\\/](bin|obj|x64|runtimes|Arkive\.C\.[^\\/]+)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) }
}
foreach ($file in ($files | Sort-Object -Unique)) {
    $source = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source file: $file" }
    $target = Join-Path $destinationPath $file
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$bad = @(Get-ChildItem -LiteralPath $destinationPath -Recurse -File | Where-Object {
    ($_.Extension -eq '.md' -and $_.Name -ne 'README.md') -or $_.Extension -in @('.upk', '.arkive', '.glb', '.dds', '.db', '.exe', '.dll', '.pdb')
})
if ($bad.Count) { throw 'Source export contains a forbidden generated or user-data file.' }
Write-Host "Exported $($files.Count) source entries to a fresh snapshot. Audit it before publishing."
