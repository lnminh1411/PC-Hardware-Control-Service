#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$ForceConfig,
    [switch]$DisableFanControl,
    [switch]$DisableOpenRGB
)

$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $false)]
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $false)]
        [int[]]$ValidExitCodes = @(0)
    )

    # Start-Process joins arguments with spaces, so quote any argument that contains whitespace.
    $quoted = foreach ($arg in $ArgumentList) {
        if ($arg -match '\s' -and $arg -notmatch '^".*"$') { '"' + $arg + '"' } else { $arg }
    }
    $proc = Start-Process -FilePath $FilePath -ArgumentList ($quoted -join ' ') -Wait -NoNewWindow -PassThru
    if ($proc.ExitCode -notin $ValidExitCodes) {
        $argStr = $ArgumentList -join ' '
        throw "Command '$FilePath $argStr' failed with exit code $($proc.ExitCode)"
    }
    return $proc.ExitCode
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallDir = "C:\Program Files\PcHwCtl"
$DataDir = "C:\ProgramData\PcHwCtl"
$ServiceExe = Join-Path $InstallDir "pchwctl.exe"
$SourceConfigFile = Join-Path $RepoRoot "config\pchwctl.json"
$TargetConfigFile = Join-Path $DataDir "pchwctl.json"
$LogFile = Join-Path $DataDir "log.txt"

# 1. Unless -SkipPublish, run publish.ps1
if (-not $SkipPublish) {
    $publishScript = Join-Path $PSScriptRoot "publish.ps1"
    Write-Host "Running publish script: $publishScript"
    & $publishScript
}

# 2. Stop service PcHwCtl if it exists (wait for Stopped, 15 s max)
$svc = Get-Service -Name "PcHwCtl" -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping service PcHwCtl..."
        Stop-Service -Name "PcHwCtl" -Force
        $timeout = [System.TimeSpan]::FromSeconds(15)
        $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $timeout)
        Write-Host "Service PcHwCtl stopped."
    } else {
        Write-Host "Service PcHwCtl is already stopped."
    }
}

# Display agents (pchwctl display-agent in the user's session) run from the install directory and would lock it.
$installedExe = Join-Path $InstallDir "pchwctl.exe"
Get-Process -Name "pchwctl" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $installedExe } |
    ForEach-Object {
        Write-Host "Stopping leftover pchwctl process $($_.Id) (session $($_.SessionId))..."
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit(5000) | Out-Null
    }

# 3. Copy publish\* to C:\Program Files\PcHwCtl\ (create, overwrite)
$publishDir = Join-Path $RepoRoot "publish"
if (-not (Test-Path -LiteralPath $publishDir)) {
    throw "Publish directory does not exist: $publishDir. Run publish.ps1 or omit -SkipPublish."
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-Host "Creating install directory: $InstallDir"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

Write-Host "Copying files from $publishDir to $InstallDir..."
Copy-Item -Path (Join-Path $publishDir "*") -Destination $InstallDir -Recurse -Force

# 4. Config: copy config\pchwctl.json to C:\ProgramData\PcHwCtl\pchwctl.json
if (-not (Test-Path -LiteralPath $DataDir)) {
    Write-Host "Creating data directory: $DataDir"
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
}

$targetConfigExists = Test-Path -LiteralPath $TargetConfigFile
if (-not $targetConfigExists -or $ForceConfig) {
    if ($targetConfigExists -and $ForceConfig) {
        $timestamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
        $bakFile = Join-Path $DataDir "pchwctl.json.bak-$timestamp"
        Write-Host "Backing up existing config to $bakFile"
        Copy-Item -LiteralPath $TargetConfigFile -Destination $bakFile -Force
    }
    Write-Host "Copying config template from $SourceConfigFile to $TargetConfigFile"
    Copy-Item -LiteralPath $SourceConfigFile -Destination $TargetConfigFile -Force
} else {
    Write-Host "Config file already exists at $TargetConfigFile (use -ForceConfig to overwrite)."
}

# 5. Service configuration: create or update
$svc = Get-Service -Name "PcHwCtl" -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Creating service PcHwCtl..."
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("create", "PcHwCtl", "binPath=", "`"$ServiceExe`"", "start=", "auto", "DisplayName=", "PC Hardware Control")
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("description", "PcHwCtl", "Hardware fan and RGB control service")
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("failure", "PcHwCtl", "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000")
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("failureflag", "PcHwCtl", "1")
} else {
    Write-Host "Updating existing service PcHwCtl binary path..."
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("config", "PcHwCtl", "binPath=", "`"$ServiceExe`"", "start=", "auto", "DisplayName=", "PC Hardware Control")
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("description", "PcHwCtl", "Hardware fan and RGB control service")
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("failure", "PcHwCtl", "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000")
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("failureflag", "PcHwCtl", "1")
}

# 6. -DisableFanControl
if ($DisableFanControl) {
    Write-Host "Processing -DisableFanControl..."

    # Stop process FanControl
    $fcProcesses = Get-Process -Name "FanControl" -ErrorAction SilentlyContinue
    if ($fcProcesses) {
        Write-Host "Stopping running FanControl process(es)..."
        $fcProcesses | Stop-Process -Force
    }

    # Scheduled tasks whose action path contains FanControl.exe
    $allTasks = Get-ScheduledTask -ErrorAction SilentlyContinue
    foreach ($task in $allTasks) {
        $hasAction = $false
        foreach ($action in $task.Actions) {
            if ($action.Execute -and ($action.Execute -like "*FanControl.exe*")) {
                $hasAction = $true
                break
            }
            if ($action.Arguments -and ($action.Arguments -like "*FanControl.exe*")) {
                $hasAction = $true
                break
            }
        }
        if ($hasAction) {
            Write-Host "Disabling scheduled task: $($task.TaskPath)$($task.TaskName)"
            Disable-ScheduledTask -InputObject $task | Out-Null
        }
    }

    # Registry Run keys (HKCU and HKLM)
    $runKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
    )
    foreach ($rk in $runKeys) {
        if (Test-Path -LiteralPath $rk) {
            $regKey = Get-Item -LiteralPath $rk
            foreach ($valName in $regKey.GetValueNames()) {
                if ($valName -like "*.disabled-by-pchwctl") { continue }
                $val = $regKey.GetValue($valName)
                if ($val -and ($val -like "*FanControl.exe*")) {
                    $newValName = "$valName.disabled-by-pchwctl"
                    Write-Host "Renaming registry value in $rk from '$valName' to '$newValName'"
                    Set-ItemProperty -LiteralPath $rk -Name $newValName -Value $val
                    Remove-ItemProperty -LiteralPath $rk -Name $valName
                }
            }
        }
    }

    # Startup folder shortcuts
    $startupFolders = @(
        [System.IO.Path]::Combine($env:APPDATA, "Microsoft\Windows\Start Menu\Programs\Startup"),
        [System.IO.Path]::Combine($env:ProgramData, "Microsoft\Windows\Start Menu\Programs\Startup")
    )
    $wsh = New-Object -ComObject WScript.Shell
    foreach ($folder in $startupFolders) {
        if (Test-Path -LiteralPath $folder) {
            $files = Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue
            foreach ($file in $files) {
                if ($file.Name -like "*.disabled-by-pchwctl") { continue }
                $isTarget = $false
                if ($file.Extension -eq ".lnk") {
                    try {
                        $sc = $wsh.CreateShortcut($file.FullName)
                        if ($sc.TargetPath -and ($sc.TargetPath -like "*FanControl.exe*")) {
                            $isTarget = $true
                        }
                    } catch {
                        # ignore shortcut read errors
                    }
                } elseif ($file.Name -like "*FanControl*") {
                    $isTarget = $true
                }
                if ($isTarget) {
                    $newFileName = "$($file.FullName).disabled-by-pchwctl"
                    Write-Host "Renaming startup shortcut from '$($file.FullName)' to '$newFileName'"
                    Move-Item -LiteralPath $file.FullName -Destination $newFileName -Force
                }
            }
        }
    }
}

# 7. -DisableOpenRGB
if ($DisableOpenRGB) {
    Write-Host "Processing -DisableOpenRGB..."

    # Stop process OpenRGB
    $openRgbProcs = Get-Process -Name "OpenRGB" -ErrorAction SilentlyContinue
    if ($openRgbProcs) {
        Write-Host "Stopping running OpenRGB process(es)..."
        $openRgbProcs | Stop-Process -Force
    }

    # Scheduled tasks
    $allTasks = Get-ScheduledTask -ErrorAction SilentlyContinue
    foreach ($task in $allTasks) {
        $hasAction = $false
        foreach ($action in $task.Actions) {
            if ($action.Execute -and ($action.Execute -like "*OpenRGB.exe*")) {
                $hasAction = $true
                break
            }
            if ($action.Arguments -and ($action.Arguments -like "*OpenRGB.exe*")) {
                $hasAction = $true
                break
            }
        }
        if ($hasAction) {
            Write-Host "Disabling scheduled task: $($task.TaskPath)$($task.TaskName)"
            Disable-ScheduledTask -InputObject $task | Out-Null
        }
    }

    # Registry Run keys (HKCU and HKLM)
    $runKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
    )
    foreach ($rk in $runKeys) {
        if (Test-Path -LiteralPath $rk) {
            $regKey = Get-Item -LiteralPath $rk
            foreach ($valName in $regKey.GetValueNames()) {
                if ($valName -like "*.disabled-by-pchwctl") { continue }
                $val = $regKey.GetValue($valName)
                if ($val -and ($val -like "*OpenRGB.exe*")) {
                    $newValName = "$valName.disabled-by-pchwctl"
                    Write-Host "Renaming registry value in $rk from '$valName' to '$newValName'"
                    Set-ItemProperty -LiteralPath $rk -Name $newValName -Value $val
                    Remove-ItemProperty -LiteralPath $rk -Name $valName
                }
            }
        }
    }

    # Startup folder shortcuts
    $startupFolders = @(
        [System.IO.Path]::Combine($env:APPDATA, "Microsoft\Windows\Start Menu\Programs\Startup"),
        [System.IO.Path]::Combine($env:ProgramData, "Microsoft\Windows\Start Menu\Programs\Startup")
    )
    $wsh = New-Object -ComObject WScript.Shell
    foreach ($folder in $startupFolders) {
        if (Test-Path -LiteralPath $folder) {
            $files = Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue
            foreach ($file in $files) {
                if ($file.Name -like "*.disabled-by-pchwctl") { continue }
                $isTarget = $false
                if ($file.Extension -eq ".lnk") {
                    try {
                        $sc = $wsh.CreateShortcut($file.FullName)
                        if ($sc.TargetPath -and ($sc.TargetPath -like "*OpenRGB.exe*")) {
                            $isTarget = $true
                        }
                    } catch {
                        # ignore shortcut read errors
                    }
                } elseif ($file.Name -like "*OpenRGB*") {
                    $isTarget = $true
                }
                if ($isTarget) {
                    $newFileName = "$($file.FullName).disabled-by-pchwctl"
                    Write-Host "Renaming startup shortcut from '$($file.FullName)' to '$newFileName'"
                    Move-Item -LiteralPath $file.FullName -Destination $newFileName -Force
                }
            }
        }
    }

    # Any Windows service whose binary path contains OpenRGB.exe
    $services = Get-CimInstance -ClassName Win32_Service -Filter "PathName LIKE '%OpenRGB.exe%'" -ErrorAction SilentlyContinue
    if ($services) {
        foreach ($s in $services) {
            Write-Host "Stopping service $($s.Name) and setting start mode to Disabled..."
            if ($s.State -ne "Stopped") {
                Stop-Service -Name $s.Name -Force -ErrorAction SilentlyContinue
            }
            Set-Service -Name $s.Name -StartupType Disabled
            Write-Host "Service $($s.Name) set to Disabled."
        }
    }
}

# 8. Start the service, wait for Running (15 s), print sc query and last 20 lines of log
Write-Host "Starting service PcHwCtl..."
Start-Service -Name "PcHwCtl"
$svc = Get-Service -Name "PcHwCtl"
$timeout = [System.TimeSpan]::FromSeconds(15)
$svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, $timeout)
Write-Host "Service PcHwCtl is Running."

Write-Host "`n--- Service Query Status ---"
& sc.exe query PcHwCtl

if (Test-Path -LiteralPath $LogFile) {
    Write-Host "`n--- Last 20 lines of $LogFile ---"
    Get-Content -LiteralPath $LogFile -Tail 20
} else {
    Write-Host "Log file $LogFile does not exist yet."
}

Write-Host "`nInstallation finished successfully."
