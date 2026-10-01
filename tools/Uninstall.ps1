<#
.SYNOPSIS
  Removes Screen Time installed by Install.ps1.

.DESCRIPTION
  Asks the sampler to stop cleanly (it writes its in-flight span on the way
  out) and closes the app, both by the path they run from, never by name;
  then removes the scheduled task, the program folder, the Start menu
  shortcut, the start-at-login entry and the Installed apps entry. Nothing
  needs administrator approval.

  THE HISTORY IS NEVER TOUCHED. The data folder (the database, its backup,
  logos, settings) is the thing this app exists to keep; delete it by hand if
  you mean to. -RemoveData removes only %LOCALAPPDATA%\Screen Time Native:
  preferences, logs, and the sampler's spans not yet saved.

  Install.ps1 copies this script into the program folder and registers it as
  the uninstall command, so Installed apps can remove the app without the
  source repo. Run from there, it first copies itself to %TEMP% and hands
  over, because it is about to delete the folder it lives in.

  Keep this file pure ASCII.
#>
param(
  [switch]$RemoveData,
  # No final pause - for Install.ps1 -Uninstall and scripted use.
  [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\Screen Time'
$exe = Join-Path $target 'ScreenTime.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Screen Time.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenTimeNative'
$localDir = Join-Path $env:LOCALAPPDATA 'Screen Time Native'
$samplerDir = Join-Path $localDir 'sampler'
$taskName = 'Screen Time Native Sampler'

# Running from inside the folder about to be deleted: continue from a copy.
if ($PSCommandPath -and $PSCommandPath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
  $copy = Join-Path $env:TEMP "ScreenTime-Uninstall-$PID.ps1"
  Copy-Item $PSCommandPath $copy -Force
  $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$copy`"")
  if ($RemoveData) { $forward += '-RemoveData' }
  if ($Quiet) { $forward += '-Quiet' }
  Set-Location $env:TEMP
  $p = Start-Process powershell.exe -ArgumentList $forward -Wait -PassThru
  Remove-Item $copy -Force -ErrorAction SilentlyContinue
  exit $p.ExitCode
}

try {
  # The task first, so nothing restarts the sampler while it is stopping.
  if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed scheduled task '$taskName'."
  }

  $sampling = @(Get-CimInstance Win32_Process -Filter "Name='ScreenTime.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -ieq $exe -and $_.CommandLine -like '*--sample*' })
  if ($sampling.Count -gt 0) {
    New-Item -ItemType Directory -Force $samplerDir | Out-Null
    [IO.File]::WriteAllText((Join-Path $samplerDir 'sampler.stop'), '')
    Write-Host 'Asking the sampler to stop (it saves what it is recording first)...'
    foreach ($p in $sampling) { Wait-Process -Id $p.ProcessId -Timeout 20 -ErrorAction SilentlyContinue }
  }
  Get-Process ScreenTime -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }

  if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
  $run = Get-ItemProperty -Path $runKey -Name 'Screen Time' -ErrorAction SilentlyContinue
  if ($run -and $run.'Screen Time' -like "*$exe*") { Remove-ItemProperty -Path $runKey -Name 'Screen Time' }
  if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }

  $dataDir = $null
  $location = Join-Path $localDir 'location.json'
  if (Test-Path $location) { $dataDir = (Get-Content $location -Raw | ConvertFrom-Json).dataDir }
  if ($RemoveData -and (Test-Path $localDir)) {
    Remove-Item $localDir -Recurse -Force
    Write-Host "Removed $localDir"
  }
  Write-Host 'Screen Time is uninstalled.' -ForegroundColor Green
  if ($dataDir) { Write-Host "Your history in $dataDir was kept. Reinstalling and pointing at it brings it all back." }
  $code = 0
}
catch {
  Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
  $code = 1
}

# Launched from Installed apps, this is a console window of its own: leave the
# result on screen long enough to read.
if (-not $Quiet) { Start-Sleep -Seconds 5 }
exit $code
