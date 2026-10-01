<#
.SYNOPSIS
  Registers (or removes) the app's one scheduled task. NO ELEVATION NEEDED.
  Install.ps1 and Uninstall.ps1 run it for you.

.DESCRIPTION
  ONE TASK, UNELEVATED

    Screen Time Native Sampler    at every sign-in, as you, not elevated.
                                  Runs the installed ScreenTime.exe --sample,
                                  which records the foreground app every 2 s
                                  and saves to the database every 15 minutes.

  Reading which window is in front is something any program in the session
  may do, so nothing here needs Administrator - unlike Data Usage Native,
  whose SRUM snapshot does.

  ExecutionTimeLimit is ZERO, meaning none: Task Scheduler's default kills a
  task after three days, and the symptom would be screen time that simply
  stops being recorded on a machine left running, with no error anywhere.
  The exe is a WinExe, so the task opens no window and needs no launcher.

  The task lives in the Windows task store on C:\, which a reset wipes;
  after a reset, re-run Install.ps1.

  Keep this file pure ASCII: Windows PowerShell 5.1 reads a BOM-less script
  as ANSI.

.PARAMETER Exe
  The installed ScreenTime.exe the task runs.
.PARAMETER Start
  Start the sampler straight away rather than at the next sign-in.
#>
param(
  [string]$Exe,
  [switch]$Start,
  [switch]$Unregister
)
$ErrorActionPreference = 'Stop'

$taskName = 'Screen Time Native Sampler'

if ($Unregister) {
  if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed scheduled task '$taskName'."
  }
  exit 0
}

if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw "The app is not at '$Exe'." }

$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
# RestartCount brings a crashed sampler back within a minute. StartWhenAvailable
# is deliberately NOT set: a missed sign-in is not something to catch up on.
$settings = New-ScheduledTaskSettingsSet `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -DontStopOnIdleEnd `
  -MultipleInstances IgnoreNew `
  -ExecutionTimeLimit ([TimeSpan]::Zero) `
  -RestartCount 3 `
  -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName `
  -Action (New-ScheduledTaskAction -Execute $Exe -Argument '--sample' -WorkingDirectory (Split-Path $Exe)) `
  -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) `
  -Principal (New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited) `
  -Settings $settings `
  -Description 'Records which application is in the foreground, for Screen Time. Runs unelevated; captures no window titles.' `
  -Force | Out-Null
Write-Host "Registered '$taskName' - at sign-in, not elevated, no time limit."

if ($Start) {
  Start-ScheduledTask -TaskName $taskName
  Write-Host 'Started the sampler.'
}
exit 0
