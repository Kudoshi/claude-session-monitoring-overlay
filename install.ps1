# Installs Claude Overlay for the current user (no admin rights needed).
# Copies ClaudeOverlay.exe to %LOCALAPPDATA%\ClaudeOverlay\app, adds a Start menu shortcut, and starts it.
# Run from the extracted folder:  powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'ClaudeOverlay.exe'
if (-not (Test-Path $src)) { throw "ClaudeOverlay.exe not found next to install.ps1 ($PSScriptRoot)." }

$appDir = Join-Path $env:LOCALAPPDATA 'ClaudeOverlay\app'
$exe = Join-Path $appDir 'ClaudeOverlay.exe'

# Stop a running copy so the file can be replaced.
Get-Process -Name ClaudeOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item $src $exe -Force

# Start menu shortcut
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Claude Overlay.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $appDir
$s.Description = 'Claude session overlay'
$s.Save()

Write-Host ''
Write-Host 'Claude Overlay installed.' -ForegroundColor Green
Write-Host "  App:       $exe"
Write-Host "  Shortcut:  Start menu > Claude Overlay"
Write-Host ''
Write-Host 'Starting it now. Ctrl+click the gear or double-click the tray icon to open Settings,'
Write-Host 'then turn on the Claude Desktop hook for live Needs input and exact Running/Done states.'
Start-Process $exe
