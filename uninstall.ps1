# Removes Claude Overlay: stops it, removes the Claude Code / Desktop hook entries from
# ~/.claude/settings.json, deletes the Start menu shortcut and the app folder.
# Your settings and status files in %LOCALAPPDATA%\ClaudeOverlay are kept unless you pass -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Continue'

$appDir = Join-Path $env:LOCALAPPDATA 'ClaudeOverlay\app'
$exe = Join-Path $appDir 'ClaudeOverlay.exe'

Get-Process -Name ClaudeOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path $exe) { & $exe --uninstall-hook | Out-Null }

Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Claude Overlay.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'ClaudeOverlay\app') -Recurse -Force -ErrorAction SilentlyContinue
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $run -Name ClaudeOverlay -ErrorAction SilentlyContinue

if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'ClaudeOverlay') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'Claude Overlay removed.' -ForegroundColor Green
