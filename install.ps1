# Installs Claude Session Monitoring Overlay for the current user (no admin rights needed).
# Copies ClaudeSessionMonitoringOverlay.exe to %LOCALAPPDATA%\ClaudeSessionMonitoringOverlay\app,
# adds a Start menu shortcut, and starts it. Upgrading from "Claude Overlay" 1.0.x is handled here.
# Run from the extracted folder:  powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'ClaudeSessionMonitoringOverlay.exe'
if (-not (Test-Path $src)) { throw "ClaudeSessionMonitoringOverlay.exe not found next to install.ps1 ($PSScriptRoot)." }

$appDir = Join-Path $env:LOCALAPPDATA 'ClaudeSessionMonitoringOverlay\app'
$exe = Join-Path $appDir 'ClaudeSessionMonitoringOverlay.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = Join-Path $startMenu 'Claude Session Monitoring Overlay.lnk'

# Stop running copies (old and new) so files can be replaced.
Get-Process -Name ClaudeOverlay, ClaudeSessionMonitoringOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Old install (1.0.x): remove its hook entries first so they don't point at a deleted file.
# The new app re-adds them at startup if the hook is still switched on in Settings.
$oldDir = Join-Path $env:LOCALAPPDATA 'ClaudeOverlay\app'
$oldExe = Join-Path $oldDir 'ClaudeOverlay.exe'
if (Test-Path $oldExe) { & $oldExe --uninstall-hook | Out-Null }
Remove-Item $oldDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $startMenu 'Claude Overlay.lnk') -Force -ErrorAction SilentlyContinue
# The old 'Start with Windows' value is moved by the app itself at first launch, so it isn't removed here.

New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item $src $exe -Force

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $appDir
$s.Description = 'Claude session monitoring overlay'
$s.Save()

Write-Host ''
Write-Host 'Claude Session Monitoring Overlay installed.' -ForegroundColor Green
Write-Host "  App:       $exe"
Write-Host '  Shortcut:  Start menu > Claude Session Monitoring Overlay'
Write-Host ''
Write-Host 'Starting it now. Ctrl+click the gear or double-click the tray icon to open Settings,'
Write-Host 'then turn on the Claude Desktop hook for live Needs input and exact Running/Done states.'
Start-Process $exe
