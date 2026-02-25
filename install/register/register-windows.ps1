#Requires -Version 5.1
<#
.SYNOPSIS
    VolMon registration script for Windows.

.DESCRIPTION
    Registers the daemon as a Windows Task Scheduler task that runs at
    logon (hidden, auto-restart on failure) and places a shortcut to the
    GUI in the user's Startup folder so it launches on login.

    Optionally registers the hardware daemon and hardware GUI when
    -IncludeHardware is passed.

    Run from inside the publish\win-x64\ folder (or wherever the
    VolMon binaries are located).

.PARAMETER IncludeHardware
    Also register the hardware daemon and hardware GUI.

.PARAMETER Unregister
    Remove the scheduled tasks and startup shortcuts.

.EXAMPLE
    .\register.ps1
    .\register.ps1 -IncludeHardware
    .\register.ps1 -Unregister
#>
param(
    [switch]$IncludeHardware,
    [switch]$Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir        = Split-Path -Parent $MyInvocation.MyCommand.Definition
$DaemonExe        = Join-Path $ScriptDir 'VolMon.Daemon.exe'
$GuiExe           = Join-Path $ScriptDir 'VolMon.GUI.exe'
$HardwareExe      = Join-Path $ScriptDir 'VolMon.Hardware.exe'
$HardwareGuiExe   = Join-Path $ScriptDir 'VolMon.HardwareGUI.exe'
$IconFile         = Join-Path $ScriptDir 'volmon.ico'
$TaskName         = 'VolMon Daemon'
$HardwareTaskName = 'VolMon Hardware Daemon'
$StartupDir       = [Environment]::GetFolderPath('Startup')
$GuiShortcut      = Join-Path $StartupDir 'VolMon GUI.lnk'
$HwGuiShortcut    = Join-Path $StartupDir 'VolMon Hardware Manager.lnk'

# ── Unregister ────────────────────────────────────────────────────────
if ($Unregister) {
    Write-Host 'Unregistering VolMon...' -ForegroundColor Cyan

    # Remove scheduled tasks
    foreach ($name in @($TaskName, $HardwareTaskName)) {
        $existing = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if ($existing) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            Write-Host "  Task '$name' removed." -ForegroundColor Green
        } else {
            Write-Host "  Task '$name' not found (already removed)." -ForegroundColor Yellow
        }
    }

    # Remove startup shortcuts
    foreach ($path in @($GuiShortcut, $HwGuiShortcut)) {
        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Host "  Shortcut removed: $(Split-Path -Leaf $path)" -ForegroundColor Green
        }
    }

    Write-Host "`nVolMon unregistered." -ForegroundColor Green
    exit 0
}

# ── Validate binaries ────────────────────────────────────────────────
$RequiredBins = @($DaemonExe, $GuiExe)
if ($IncludeHardware) {
    $RequiredBins += @($HardwareExe, $HardwareGuiExe)
}

foreach ($bin in $RequiredBins) {
    if (-not (Test-Path $bin)) {
        Write-Host "Error: $(Split-Path -Leaf $bin) not found in $ScriptDir" -ForegroundColor Red
        Write-Host 'Run this script from the publish\win-x64\ folder.' -ForegroundColor Red
        exit 1
    }
}

# ── Helper: register a scheduled task ────────────────────────────────
function Register-DaemonTask {
    param(
        [string]$Name,
        [string]$ExePath,
        [string]$Description
    )

    # Remove old task if it exists
    $existing = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($existing) {
        Stop-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $Name -Confirm:$false
    }

    $action  = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $ScriptDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -Hidden

    Register-ScheduledTask `
        -TaskName $Name `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Description $Description `
        -RunLevel Limited | Out-Null

    Start-ScheduledTask -TaskName $Name
    Write-Host "  Task '$Name' installed and started." -ForegroundColor Green
}

# ── Helper: create a startup shortcut ────────────────────────────────
function New-StartupShortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetExe,
        [string]$Description
    )

    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath       = $TargetExe
    $shortcut.WorkingDirectory = $ScriptDir
    $shortcut.Description      = $Description
    $shortcut.WindowStyle      = 1  # Normal window
    if (Test-Path $IconFile) {
        $shortcut.IconLocation = "$IconFile,0"
    }
    $shortcut.Save()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

# ── Daemon: Task Scheduler ───────────────────────────────────────────
Write-Host 'Installing daemon scheduled task...' -ForegroundColor Cyan
Register-DaemonTask -Name $TaskName -ExePath $DaemonExe `
    -Description 'VolMon Audio Group Volume Daemon'

# ── Hardware Daemon: Task Scheduler (optional) ───────────────────────
if ($IncludeHardware) {
    Write-Host 'Installing hardware daemon scheduled task...' -ForegroundColor Cyan
    Register-DaemonTask -Name $HardwareTaskName -ExePath $HardwareExe `
        -Description 'VolMon Hardware Daemon'
}

# ── GUI: Startup folder shortcut ─────────────────────────────────────
Write-Host 'Installing GUI startup shortcut...' -ForegroundColor Cyan
New-StartupShortcut -ShortcutPath $GuiShortcut -TargetExe $GuiExe `
    -Description 'VolMon Volume Monitoring and Control'
Write-Host '  GUI will start automatically on next login.' -ForegroundColor Green

# ── Hardware GUI: Startup folder shortcut (optional) ─────────────────
if ($IncludeHardware) {
    Write-Host 'Installing Hardware GUI startup shortcut...' -ForegroundColor Cyan
    New-StartupShortcut -ShortcutPath $HwGuiShortcut -TargetExe $HardwareGuiExe `
        -Description 'VolMon Hardware Device Manager'
    Write-Host '  Hardware GUI will start automatically on next login.' -ForegroundColor Green
}

# ── Done ──────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'VolMon registered successfully!' -ForegroundColor Green
Write-Host ''
Write-Host "  Daemon task:    Get-ScheduledTask -TaskName '$TaskName'"
if ($IncludeHardware) {
    Write-Host "  Hardware task:  Get-ScheduledTask -TaskName '$HardwareTaskName'"
}
Write-Host "  Start GUI:      $GuiExe"
if ($IncludeHardware) {
    Write-Host "  Hardware GUI:   $HardwareGuiExe"
}
Write-Host "  Unregister:     .\register.ps1 -Unregister"
if (-not $IncludeHardware) {
    Write-Host ''
    Write-Host '  Hardware was not registered. To include it, re-run with -IncludeHardware'
}
Write-Host ''
