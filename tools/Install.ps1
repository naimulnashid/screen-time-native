<#
.SYNOPSIS
  Builds the app and installs it for the current user. No administrator
  approval at any step.

.DESCRIPTION
  1. Publishes a self-contained Release build (it carries its own .NET and
     Windows App SDK) into %LOCALAPPDATA%\Programs\Screen Time. A running
     sampler is asked to stop cleanly first - it writes its in-flight span on
     the way out - and a running app is closed, both by the path they run
     from, never by name. From a release zip, which carries that build in
     app\ beside this script, nothing is built: the build is copied as it is.
  2. Adds "Screen Time" to the Start menu.
  3. Registers it under Settings -> Apps -> Installed apps (and Control
     Panel's Programs and Features), per user, with Uninstall.ps1 copied into
     the program folder as its uninstall command - so it can be removed
     without this repo.
  4. Points the app at its data folder (-DataDir), unless one is already set.
  5. Registers the sampler's sign-in task (tools\Register-Tasks.ps1) and
     starts it, so recording begins now rather than at the next sign-in.

  Re-running updates the installed copy and re-registers the task. After a
  Windows reset, re-running it is the whole recovery: point -DataDir at the
  same folder and the history is back.

  Keep this file pure ASCII: Windows PowerShell 5.1 reads a BOM-less script
  as ANSI.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -Uninstall
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install.ps1    # inside an extracted release zip
#>
param(
  [string]$DataDir = 'D:\PersistentData\screen-time-native',
  [switch]$Uninstall,
  [switch]$RemoveData,
  [switch]$SkipTasks
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Screen Time'
$exe = Join-Path $target 'ScreenTime.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Screen Time.lnk'
$localDir = Join-Path $env:LOCALAPPDATA 'Screen Time Native'
$samplerDir = Join-Path $localDir 'sampler'

if ($Uninstall) {
  # One implementation, shared with the Installed apps entry.
  & (Join-Path $PSScriptRoot 'Uninstall.ps1') -RemoveData:$RemoveData -Quiet
  return
}

function Stop-Installed {
  # The sampler first, politely: a stop file makes it leave its loop and write
  # the span in flight. Killing it would lose that span until the next start
  # recovered it from the heartbeat.
  $running = @(Get-CimInstance Win32_Process -Filter "Name='ScreenTime.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -ieq $exe -and $_.CommandLine -like '*--sample*' })
  if ($running.Count -gt 0) {
    New-Item -ItemType Directory -Force $samplerDir | Out-Null
    [IO.File]::WriteAllText((Join-Path $samplerDir 'sampler.stop'), '')
    Write-Host 'Asking the sampler to stop (it saves what it is recording first)...'
    foreach ($p in $running) { Wait-Process -Id $p.ProcessId -Timeout 20 -ErrorAction SilentlyContinue }
  }
  Get-Process ScreenTime -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }
}

# ---- 1. Publish and copy ------------------------------------------------------
# A release zip is this script, Uninstall.ps1 and Register-Tasks.ps1 beside
# app\, the published build. Anywhere else this is a clone, and it builds.
$packaged = Test-Path (Join-Path $PSScriptRoot 'app\ScreenTime.exe')
if ($packaged) {
  $staging = Join-Path $PSScriptRoot 'app'
  Write-Host "Installing the build in $staging"
} else {
  $staging = Join-Path $root 'publish\ScreenTime'
  if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
  Write-Host 'Publishing a Release build...'
  & dotnet publish (Join-Path $root 'src\ScreenTime.App\ScreenTime.App.csproj') -c Release -o $staging --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
# Without its own resources.pri the app dies at startup (0xC000027B) - what a
# publish without EnableMsixTooling produced. Never install that.
if (-not (Test-Path (Join-Path $staging 'ScreenTime.pri'))) {
  throw 'The published build has no ScreenTime.pri, so it would crash at startup. Check EnableMsixTooling in ScreenTime.App.csproj.'
}

Stop-Installed
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $staging '*') $target -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $target 'Uninstall.ps1') -Force
Copy-Item (Join-Path $PSScriptRoot 'Register-Tasks.ps1') (Join-Path $target 'Register-Tasks.ps1') -Force
# Explorer marks every file it extracts from a downloaded zip as downloaded,
# and SmartScreen then stops the app at each launch. Running this script is
# the decision to install it, as it is for any installer: clear the mark on
# the installed copy only.
if ($packaged) { Get-ChildItem $target -Recurse -File | Unblock-File }

# ---- 2. Start menu -------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'A permanent history of which app was in front on this PC, and for how long'
$link.Save()

# ---- 3. Installed apps -----------------------------------------------------------
$bytes = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum
# From the exe, not Directory.Build.props: a release zip has no props file.
$version = ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion).ToString(3)
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall.ps1')`""
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenTimeNative'
New-Item -Path $key -Force | Out-Null
$entry = @{
  DisplayName = 'Screen Time'
  DisplayVersion = [string]$version
  Publisher = 'Naimul Nashid'
  DisplayIcon = "$exe,0"
  InstallLocation = $target
  UninstallString = $uninstallCommand
  QuietUninstallString = "$uninstallCommand -Quiet"
  URLInfoAbout = 'https://github.com/naimulnashid/screen-time-native'
  InstallDate = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $entry.Keys) { New-ItemProperty -Path $key -Name $name -Value $entry[$name] -PropertyType String -Force | Out-Null }
# No Modify or Repair buttons: re-running this script is the repair.
foreach ($name in 'NoModify', 'NoRepair') { New-ItemProperty -Path $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
New-ItemProperty -Path $key -Name 'EstimatedSize' -Value ([int]($bytes / 1KB)) -PropertyType DWord -Force | Out-Null

# ---- 4. The data folder -----------------------------------------------------------
$location = Join-Path $localDir 'location.json'
if (Test-Path $location) {
  Write-Host "Data folder already set: $((Get-Content $location -Raw | ConvertFrom-Json).dataDir)"
} elseif ($DataDir) {
  $systemRoot = [IO.Path]::GetPathRoot($env:SystemRoot)
  $dataRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($DataDir))
  if ($dataRoot -ieq $systemRoot) {
    Write-Host "Not setting ${DataDir}: it is on the system drive, which a reset erases. The app will ask on first run." -ForegroundColor Yellow
  } elseif (-not (Test-Path -LiteralPath $dataRoot)) {
    # The default names D:, which not every PC has.
    Write-Host "Not setting ${DataDir}: there is no $dataRoot drive. The app will ask on first run, or re-run with -DataDir." -ForegroundColor Yellow
  } else {
    New-Item -ItemType Directory -Force $DataDir | Out-Null
    New-Item -ItemType Directory -Force $localDir | Out-Null
    $json = @{ dataDir = [IO.Path]::GetFullPath($DataDir); allowSystemDrive = $false } | ConvertTo-Json
    # BOM-less: Windows PowerShell's utf8 writes a BOM.
    [IO.File]::WriteAllText($location, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Data folder: $DataDir"
  }
}

# ---- 5. The sampler's task, and recording from now -------------------------------
if (-not $SkipTasks) {
  & (Join-Path $target 'Register-Tasks.ps1') -Exe $exe -Start
  if ($LASTEXITCODE -ne 0) { throw "Registering the sampler's task failed (exit $LASTEXITCODE). Nothing will be recorded until it succeeds: re-run this script." }
}

Write-Host ("Installed to {0} ({1:N0} MB) with a Start menu shortcut and an Installed apps entry." -f $target, ($bytes / 1MB)) -ForegroundColor Green
