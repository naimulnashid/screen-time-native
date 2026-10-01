<#
.SYNOPSIS
  Photographs several views of the app, one launch each.

.DESCRIPTION
  Each entry is "name=page[,appKey],scroll" (see SCREENTIME_DEBUG_VIEW in
  MainWindow), optionally "@x:y" to hover at a point before the capture.
  Runs against -DataDir with its own SCREENTIME_DATA_DIR and a separate local
  folder, so neither the real history nor your preferences are touched.
  Defaults to .\demo-data (make it with `screentime demo-data demo-data`).
  Screenshots land in screenshots\ (gitignored).
#>
param(
  [string[]]$Views = @('overview=overview,0'),
  [string]$DataDir,
  [string]$Configuration = 'Debug',
  [int]$Width = 2160,
  [int]$Height = 1500,
  [int]$WaitSeconds = 7,
  # Capture each page top to bottom at this width (in DIPs): the app grows its
  # window to the page's full height (SCREENTIME_DEBUG_FULLPAGE). 0 = one screen.
  [int]$FullPage = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $DataDir) { $DataDir = Join-Path $root 'demo-data' }
$exe = Join-Path $root "src\ScreenTime.App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\ScreenTime.exe"
$env:SCREENTIME_DATA_DIR = $DataDir
$env:SCREENTIME_LOCAL_DIR = Join-Path $DataDir 'local'
$env:SCREENTIME_DEBUG_FULLPAGE = if ($FullPage -gt 0) { "$FullPage" } else { $null }
# @(...) around the if: a one-item result would otherwise unroll to a string,
# which cannot be splatted.
$sizing = @(if ($FullPage -gt 0) { '-AppSized' } else { '-Width', $Width, '-Height', $Height })

foreach ($view in $Views) {
  $name, $spec = $view -split '=', 2
  $hoverX = -1; $hoverY = -1; $click = $false
  # "@x:y" hovers there; "@x:y!" also clicks, and captures what that opens.
  if ($spec -match '^(.*)@(\d+):(\d+)(!?)$') { $spec = $Matches[1]; $hoverX = [int]$Matches[2]; $hoverY = [int]$Matches[3]; $click = $Matches[4] -eq '!' }
  $clickArg = @(if ($click) { '-Click' })
  $env:SCREENTIME_DEBUG_VIEW = $spec
  # The demo has no sampler, so the Sync page would read "not running": give
  # it a heartbeat a moment old, recording an invented app. Demo data only -
  # never a real data folder.
  if (Test-Path (Join-Path $DataDir '.screen-time-demo')) {
    $samplerDir = Join-Path $DataDir 'local\sampler'
    New-Item -ItemType Directory -Force $samplerDir | Out-Null
    $now = (Get-Date).ToUniversalTime()
    $hb = '{"updated":"' + $now.ToString('yyyy-MM-ddTHH:mm:ss.fffZ') + '","started":"' + $now.AddHours(-3).ToString('yyyy-MM-ddTHH:mm:ss.fffZ') +
      '","interval_seconds":2,"closed":false,"in_flight":{"start":"' + $now.AddMinutes(-4).ToString('yyyy-MM-ddTHH:mm:ss.fffZ') +
      '","kind":"app","app":"C:\\Users\\demo\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe","unresolved":false,"ms":240000}}'
    [IO.File]::WriteAllText((Join-Path $samplerDir 'sampler-status.json'), $hb, (New-Object Text.UTF8Encoding($false)))
  }
  # Windows PowerShell 5.1, which has System.Drawing built in.
  powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Capture-Window.ps1') -Exe $exe -Out (Join-Path $root "screenshots\$name.png") @sizing -WaitSeconds $WaitSeconds -HoverX $hoverX -HoverY $hoverY @clickArg -Close
  Start-Sleep -Milliseconds 800
}
