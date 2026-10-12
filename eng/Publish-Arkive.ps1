[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = '',
    [string]$OutputDirectory,
    [string]$CertificateThumbprint,
    [switch]$SkipVerification
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'dist\Arkive.App'
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist'))
$distPrefix = $distRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output must remain beneath $distRoot"
}

if (-not $SkipVerification) {
    & (Join-Path $PSScriptRoot 'Verify-Arkive.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Release verification failed.' }
}

$staging = Join-Path $distRoot ('.publish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    $appProject = Join-Path $root 'src\Arkive.App\Arkive.App.csproj'
    $restoreArguments = @('restore', $appProject, '--configfile', (Join-Path $root 'NuGet.Config'))
    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $restoreArguments += @('-r', $RuntimeIdentifier)
    }
    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore for publishing failed.' }

    $publishArguments = @(
        'publish', $appProject,
        '-c', $Configuration,
        '--self-contained', 'false',
        '--no-restore',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $staging)
    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $publishArguments += @(
            '-r', $RuntimeIdentifier,
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true')
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Get-ChildItem -LiteralPath $staging -Filter '*.pdb' -File -Recurse |
        Remove-Item -Force

    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $certificate = Get-Item -LiteralPath (
            'Cert:\CurrentUser\My\' + $CertificateThumbprint.Replace(' ', ''))
        Get-ChildItem -LiteralPath $staging -File -Recurse |
            Where-Object Extension -In '.exe', '.dll' |
            ForEach-Object {
                $signature = Set-AuthenticodeSignature `
                    -FilePath $_.FullName `
                    -Certificate $certificate `
                    -HashAlgorithm SHA256 `
                    -TimestampServer 'http://timestamp.digicert.com'
                if ($signature.Status -ne 'Valid') {
                    throw "Signing failed for $($_.Name): $($signature.StatusMessage)"
                }
            }
    }

    $files = Get-ChildItem -LiteralPath $staging -File -Recurse |
        Sort-Object FullName
    $integrity = foreach ($file in $files) {
        $authenticode = if ($file.Extension -in '.exe', '.dll') {
            (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status.ToString()
        }
        else {
            'NotApplicable'
        }
        [pscustomobject]@{
            Path = [System.IO.Path]::GetRelativePath($staging, $file.FullName).Replace('\', '/')
            Bytes = $file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Authenticode = $authenticode
        }
    }
    $commit = (& git -C $root rev-parse HEAD 2>$null)
    [pscustomobject]@{
        SchemaVersion = 1
        CreatedAtUtc = [DateTimeOffset]::UtcNow
        GitCommit = if ($LASTEXITCODE -eq 0) { $commit } else { $null }
        Configuration = $Configuration
        RuntimeIdentifier = $RuntimeIdentifier
        Files = @($integrity)
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $staging 'release-integrity.json') -Encoding utf8NoBOM

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $output
    $staging = $null
    Write-Host "Arkive release published to $output"
}
finally {
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
