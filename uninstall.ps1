# Removes Claude Session Monitoring Overlay: stops it, removes the hook entries from ~/.claude/settings.json,
# deletes the Start menu shortcut and the app folder. Also cleans up a 1.0.x "Claude Overlay" install.
# Your settings and status files in %LOCALAPPDATA%\ClaudeOverlay are kept unless you pass -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Continue'

Get-Process -Name ClaudeOverlay, ClaudeSessionMonitoringOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$newExe = Join-Path $env:LOCALAPPDATA 'ClaudeSessionMonitoringOverlay\app\ClaudeSessionMonitoringOverlay.exe'
$oldExe = Join-Path $env:LOCALAPPDATA 'ClaudeOverlay\app\ClaudeOverlay.exe'
if (Test-Path $newExe) { & $newExe --uninstall-hook | Out-Null }
elseif (Test-Path $oldExe) { & $oldExe --uninstall-hook | Out-Null }

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
Remove-Item (Join-Path $startMenu 'Claude Session Monitoring Overlay.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $startMenu 'Claude Overlay.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'ClaudeSessionMonitoringOverlay') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'ClaudeOverlay\app') -Recurse -Force -ErrorAction SilentlyContinue

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $run -Name ClaudeSessionMonitoringOverlay -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $run -Name ClaudeOverlay -ErrorAction SilentlyContinue

if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'ClaudeOverlay') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Claude Session Monitoring Overlay removed.' -ForegroundColor Green