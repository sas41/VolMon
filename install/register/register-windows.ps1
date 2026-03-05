#Requires -Version 5.1
<#
.SYNOPSIS
    VolMon registration script for Windows.

.DESCRIPTION
    Registers the daemon as a hidden Windows Task Scheduler task that runs at
    logon (auto-restart on failure), places shortcuts to the GUI in the Startup
    folder, Start Menu, and Desktop so they launch on login and are discoverable,
    and creates Start Menu / Desktop shortcuts for the GUI applications.

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
$DesktopDir       = [Environment]::GetFolderPath('Desktop')
$StartMenuDir     = Join-Path ([Environment]::GetFolderPath('Programs')) 'VolMon'
$GuiStartup       = Join-Path $StartupDir   'VolMon.lnk'
$GuiDesktop       = Join-Path $DesktopDir   'VolMon.lnk'
$GuiStartMenu     = Join-Path $StartMenuDir 'VolMon.lnk'
$HwGuiDesktop     = Join-Path $DesktopDir   'VolMon Hardware Manager.lnk'
$HwGuiStartMenu   = Join-Path $StartMenuDir 'VolMon Hardware Manager.lnk'

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

    # Remove all shortcuts (startup, desktop, start menu)
    $allShortcuts = @($GuiStartup, $GuiDesktop, $GuiStartMenu, $HwGuiDesktop, $HwGuiStartMenu)
    foreach ($path in $allShortcuts) {
        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Host "  Shortcut removed: $path" -ForegroundColor Green
        }
    }
    # Remove Start Menu folder if empty
    if ((Test-Path $StartMenuDir) -and (Get-ChildItem $StartMenuDir -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item $StartMenuDir -Force
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

# ── GUI: Startup, Desktop, and Start Menu shortcuts ──────────────────
Write-Host 'Installing GUI shortcuts...' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
New-StartupShortcut -ShortcutPath $GuiStartup   -TargetExe $GuiExe -Description 'VolMon Volume Monitoring and Control'
New-StartupShortcut -ShortcutPath $GuiDesktop   -TargetExe $GuiExe -Description 'VolMon Volume Monitoring and Control'
New-StartupShortcut -ShortcutPath $GuiStartMenu -TargetExe $GuiExe -Description 'VolMon Volume Monitoring and Control'
Write-Host '  GUI shortcuts installed (Startup, Desktop, Start Menu).' -ForegroundColor Green

# ── Hardware GUI: Desktop and Start Menu shortcuts (optional) ─────────
if ($IncludeHardware) {
    Write-Host 'Installing Hardware GUI shortcuts...' -ForegroundColor Cyan
    New-StartupShortcut -ShortcutPath $HwGuiDesktop   -TargetExe $HardwareGuiExe -Description 'VolMon Hardware Device Manager'
    New-StartupShortcut -ShortcutPath $HwGuiStartMenu -TargetExe $HardwareGuiExe -Description 'VolMon Hardware Device Manager'
    Write-Host '  Hardware GUI shortcuts installed (Desktop, Start Menu).' -ForegroundColor Green
}

# ── Done ──────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'VolMon registered successfully!' -ForegroundColor Green
Write-Host ''
Write-Host "  Daemon task:    Get-ScheduledTask -TaskName '$TaskName'"
if ($IncludeHardware) {
    Write-Host "  Hardware task:  Get-ScheduledTask -TaskName '$HardwareTaskName'"
}
Write-Host "  Start GUI:      $GuiExe  (also on Desktop and Start Menu)"
if ($IncludeHardware) {
    Write-Host "  Hardware GUI:   $HardwareGuiExe"
}
Write-Host "  Unregister:     .\register.ps1 -Unregister"
if (-not $IncludeHardware) {
    Write-Host ''
    Write-Host '  Hardware was not registered. To include it, re-run with -IncludeHardware'
}
Write-Host ''
