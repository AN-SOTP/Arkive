[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer, [string]$DatabaseArchive)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E7960AB-8D82-46F8-8BDD-4AC968058478}_is1'
if (Test-Path -LiteralPath $key) { throw 'An Arkive installation already exists. Use a clean Windows test account/machine for this installer test.' }
$test = Join-Path $root ('release-work\installer-test-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $test 'application'
$data = Join-Path $test 'data'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'backups') | Out-Null
$sentinel = Join-Path $data 'backups\preserve.sentinel'
'preserve' | Set-Content -LiteralPath $sentinel -Encoding utf8
$originalRoot = $env:ARKIVE_DATA_ROOT
$env:ARKIVE_DATA_ROOT = $data
try {
    $setup = Start-Process (Resolve-Path -LiteralPath $Installer).Path -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=""', "/DIR=`"$app`"", "/LOG=`"$test\setup.log`"") -PassThru -WindowStyle Hidden
    $setup.WaitForExit()
    if ($setup.ExitCode -ne 0) { throw "Installer returned $($setup.ExitCode)." }
    $version = & (Join-Path $app 'Arkive.Cli.exe') --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installed CLI did not run.' }
    & (Join-Path $PSScriptRoot 'Get-ExternalTools.ps1') -Destination (Join-Path $app 'tools\external')
    if ($DatabaseArchive) {
        $mapping = Join-Path $data 'staging\lpk\data2-fixed\arkive-asset-metadata.json'
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'Arkive.Cli.exe'))
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
        $start.Environment['DOTNET_ROOT'] = (Join-Path $test 'no-global-runtime')
        foreach ($argument in @('database-build', (Resolve-Path -LiteralPath $DatabaseArchive).Path, '--out', $mapping, '--json')) { $start.ArgumentList.Add($argument) }
        $builder = [Diagnostics.Process]::Start($start)
        $stdout = $builder.StandardOutput.ReadToEndAsync()
        $stderr = $builder.StandardError.ReadToEndAsync()
        $builder.WaitForExit()
        if ($builder.ExitCode -ne 0) { throw "Installed database builder failed: $($stderr.GetAwaiter().GetResult())" }
        $result = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($result.AssociationCount -le 0 -or -not (Test-Path -LiteralPath $mapping)) { throw 'Installed database builder did not create a mapping.' }
        Write-Host "PASS: installed database builder without global .NET/Python/QuickBMS ($($result.AssociationCount) references)."
    }
    $process = Start-Process (Join-Path $app 'Arkive.App.exe') -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw 'Installed GUI exited unexpectedly.' }
    } until ($process.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Installed GUI did not open.' }
    $downloads = Join-Path $data 'updates\test'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    $candidate = Join-Path $downloads ([IO.Path]::GetFileName($Installer))
    Copy-Item -LiteralPath $Installer -Destination $candidate
    $helper = Join-Path $downloads 'Arkive.Updater.exe'
    Copy-Item -LiteralPath (Join-Path $app 'Arkive.Updater.exe') -Destination $helper
    $hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
    $start = [Diagnostics.ProcessStartInfo]::new($helper)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @($candidate, $hash, $process.Id.ToString(), $app, $data)) { $start.ArgumentList.Add($argument) }
    $updater = [Diagnostics.Process]::Start($start)
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(30000)) { throw 'Test GUI did not close safely.' }
    if (-not $updater.WaitForExit(90000) -or $updater.ExitCode -ne 0) { throw 'Updater did not complete successfully.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $restarted = @(Get-Process -Name Arkive.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $app 'Arkive.App.exe') })
    } until (($restarted.Count -eq 1 -and $restarted[0].MainWindowHandle -ne 0) -or [DateTime]::UtcNow -gt $deadline)
    if ($restarted.Count -ne 1) { throw 'Updater did not reopen exactly one installed GUI.' }
    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve') { throw 'Updater changed user data.' }
    foreach ($owned in $restarted) {
        if ($owned.MainWindowHandle -eq 0 -or -not $owned.CloseMainWindow() -or -not $owned.WaitForExit(30000)) { throw 'Restarted test GUI did not close.' }
    }
    $uninstall = Start-Process (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -WindowStyle Hidden
    $uninstall.WaitForExit()
    if ($uninstall.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall failed or removed data.' }
    Write-Host "PASS: install, CLI, pinned tools, GUI, staged update, restart, data preservation and uninstall ($version)."
    Write-Host "Isolated test logs retained at $test"
} finally { $env:ARKIVE_DATA_ROOT = $originalRoot }
