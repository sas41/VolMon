#Requires -Version 5.1
<#
.SYNOPSIS
    VolMon registration script for Windows.

.DESCRIPTION
    Registers the daemon as a Windows Task Scheduler task that runs at
    logon (hidden, auto-restart on failure) and places a shortcut to
    the GUI in the user's Startup folder so it launches on login.

    Run from inside the publish\win-x64\ folder (or wherever the
    VolMon.Daemon.exe and VolMon.GUI.exe binaries are located).

.PARAMETER Unregister
    Remove the scheduled task and startup shortcut.

.EXAMPLE
    .\register.ps1
    .\register.ps1 -Unregister
#>
param(
    [switch]$Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Definition
$DaemonExe    = Join-Path $ScriptDir 'VolMon.Daemon.exe'
$GuiExe       = Join-Path $ScriptDir 'VolMon.GUI.exe'
$IconFile     = Join-Path $ScriptDir 'volmon.ico'
$TaskName     = 'VolMon Daemon'
$StartupDir   = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $StartupDir 'VolMon GUI.lnk'

# ── Unregister ────────────────────────────────────────────────────────
if ($Unregister) {
    Write-Host 'Unregistering VolMon...' -ForegroundColor Cyan

    # Remove scheduled task
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host '  Daemon task removed.' -ForegroundColor Green
    } else {
        Write-Host '  Daemon task not found (already removed).' -ForegroundColor Yellow
    }

    # Remove startup shortcut
    if (Test-Path $ShortcutPath) {
        Remove-Item $ShortcutPath -Force
        Write-Host '  GUI startup shortcut removed.' -ForegroundColor Green
    } else {
        Write-Host '  GUI shortcut not found (already removed).' -ForegroundColor Yellow
    }

    Write-Host "`nVolMon unregistered." -ForegroundColor Green
    exit 0
}

# ── Validate binaries ────────────────────────────────────────────────
foreach ($bin in @($DaemonExe, $GuiExe)) {
    if (-not (Test-Path $bin)) {
        Write-Host "Error: $(Split-Path -Leaf $bin) not found in $ScriptDir" -ForegroundColor Red
        Write-Host 'Run this script from the publish\win-x64\ folder.' -ForegroundColor Red
        exit 1
    }
}

# ── Daemon: Task Scheduler ───────────────────────────────────────────
Write-Host 'Installing daemon scheduled task...' -ForegroundColor Cyan

# Remove old task if it exists
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$action  = New-ScheduledTaskAction -Execute $DaemonExe -WorkingDirectory $ScriptDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -Hidden

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description 'VolMon Audio Group Volume Daemon' `
    -RunLevel Limited | Out-Null

# Start the task now
Start-ScheduledTask -TaskName $TaskName
Write-Host '  Daemon task installed and started.' -ForegroundColor Green

# ── GUI: Startup folder shortcut ─────────────────────────────────────
Write-Host 'Installing GUI startup shortcut...' -ForegroundColor Cyan

$shell    = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath       = $GuiExe
$shortcut.WorkingDirectory = $ScriptDir
$shortcut.Description      = 'VolMon Volume Monitoring and Control'
$shortcut.WindowStyle      = 1  # Normal window
if (Test-Path $IconFile) {
    $shortcut.IconLocation = "$IconFile,0"
}
$shortcut.Save()

# Release COM object
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

Write-Host '  GUI will start automatically on next login.' -ForegroundColor Green

# ── Done ──────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'VolMon registered successfully!' -ForegroundColor Green
Write-Host ''
Write-Host "  Daemon task:  Get-ScheduledTask -TaskName '$TaskName'"
Write-Host "  Start GUI:    $GuiExe"
Write-Host "  Unregister:   .\register.ps1 -Unregister"
Write-Host ''
