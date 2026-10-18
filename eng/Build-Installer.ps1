[CmdletBinding()]
param(
    [string]$Version,
    [string]$IsccPath,
    [string]$ToolsDirectory,
    [switch]$SkipVerification
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
# Directory.Build.props is the single source of the release version; -Version only confirms it.
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$sourceVersion = $properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if (-not $Version) { $Version = $sourceVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
if ($Version -ne $sourceVersion) { throw "Version $Version disagrees with Directory.Build.props ($sourceVersion). Update Directory.Build.props instead of overriding the version." }
if (-not $ToolsDirectory) { $ToolsDirectory = Join-Path $root 'tools\external' }
if (-not $IsccPath) {
    $candidates = @((Join-Path $root 'tools\build-tools\inno\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'))
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath) { throw 'Install Inno Setup 6.7+ or 7, or specify -IsccPath.' }
& (Join-Path $PSScriptRoot 'Get-ExternalTools.ps1') -Destination $ToolsDirectory
if (-not (Test-Path (Join-Path $root 'src\Arkive.Core\runtimes\win-x64\native\Arkive.Crunch.Native.dll'))) {
    & (Join-Path $root 'tools\Build-CrunchNative.ps1')
}
if (-not $SkipVerification) { & (Join-Path $PSScriptRoot 'Verify-Arkive.ps1') -RequireExternalCrunch }
$build = Join-Path $root ('dist\installer-build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $build 'payload'
$updater = Join-Path $build 'updater'
$output = Join-Path $root 'dist\releases'
New-Item -ItemType Directory -Force -Path $payload, $updater, $output | Out-Null
Push-Location $root
try {
    # The app and the command-line tool share one private .NET runtime in the runtime folder (see
    # Copy-PrivateRuntime.ps1); their hosts look only there, never for a .NET installed on the PC.
    foreach ($project in @('Arkive.App', 'Arkive.Cli')) {
        & dotnet publish "src/$project/$project.csproj" -c Release -r win-x64 --self-contained false -o $payload "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false `
            -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=runtime -p:PublishReferencesDocumentationFiles=false
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }
    $runtimeVersion = & (Join-Path $PSScriptRoot 'Copy-PrivateRuntime.ps1') -Destination $payload | Select-Object -Last 1
    & dotnet publish 'src/Arkive.Updater/Arkive.Updater.csproj' -c Release -r win-x64 --self-contained true -o $updater "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publishing updater failed.' }
    Copy-Item -LiteralPath (Join-Path $updater 'Arkive.Updater.exe') -Destination $payload
    $external = Join-Path $payload 'tools\external'
    New-Item -ItemType Directory -Force -Path $external | Out-Null
    $manifest = Get-Content (Join-Path $PSScriptRoot 'ExternalTools.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) { Copy-Item -LiteralPath (Join-Path $ToolsDirectory $file.name) -Destination $external }
    foreach ($package in @($manifest.packages)) {
        if (-not $package) { continue }
        $packageFolder = Join-Path $external $package.name
        New-Item -ItemType Directory -Force -Path $packageFolder | Out-Null
        foreach ($file in $package.files) { Copy-Item -LiteralPath (Join-Path (Join-Path $ToolsDirectory $package.name) $file.name) -Destination $packageFolder }
    }
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'COPYRIGHT.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'installer\arkive-installation.json') -Destination $payload
    foreach ($required in @('BouncyCastle.Cryptography.dll', 'Microsoft.Data.Sqlite.dll',
        'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Database runtime missing from installer payload: $required" }
    }
    if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Cannot determine the bundled .NET runtime version.' }
    # The runtime belongs in its folder: a runtime file next to the exes would make them start as self-contained apps.
    foreach ($stray in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
        if (Test-Path -LiteralPath (Join-Path $payload $stray)) { throw "Runtime file $stray is in the install root." }
    }
    $banner = & (Join-Path $payload 'Arkive.Cli.exe') 2>&1 | Select-Object -First 1
    if ("$banner" -notmatch '^Arkive') { throw "Arkive.Cli.exe did not start with its private runtime: $banner" }
    Invoke-WebRequest "https://raw.githubusercontent.com/dotnet/runtime/v$runtimeVersion/LICENSE.TXT" -OutFile (Join-Path $payload 'DOTNET-LICENSE.txt')
    Invoke-WebRequest "https://raw.githubusercontent.com/dotnet/runtime/v$runtimeVersion/THIRD-PARTY-NOTICES.TXT" -OutFile (Join-Path $payload 'DOTNET-THIRD-PARTY-NOTICES.txt')
    Invoke-WebRequest "https://raw.githubusercontent.com/dotnet/wpf/v$runtimeVersion/THIRD-PARTY-NOTICES.TXT" -OutFile (Join-Path $payload 'WPF-THIRD-PARTY-NOTICES.txt')
    Invoke-WebRequest 'https://raw.githubusercontent.com/jrsoftware/issrc/main/license.txt' -OutFile (Join-Path $payload 'INNO-LICENSE.txt')
    & $IsccPath /Q "/DAppVersion=$Version" "/DPayloadDirectory=$payload" "/DOutputDirectory=$output" (Join-Path $root 'installer\Arkive.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $output "Arkive-Setup-$Version-win-x64.exe"
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $output "Arkive-Setup-$Version-win-x64.sha256") -Encoding utf8
    Write-Host "Installer ready: $installer"
} finally { Pop-Location }
