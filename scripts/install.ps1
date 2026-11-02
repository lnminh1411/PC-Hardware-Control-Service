#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$ForceConfig,
    [switch]$DisableFanControl,
    [switch]$DisableOpenRGB,
    # The data directory holds files a non-admin owns (an older install, or planted): keep them after you checked them.
    [switch]$TrustExistingData
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

# Junction checks, ACLs, safe deletes. An elevated New-Item leaves the installing user as owner and, through CREATOR
# OWNER, with FullControl, so unelevated code could replace what the SYSTEM service loads: Set-AdminOnlyAcl fixes that.
. (Join-Path $PSScriptRoot "acl.ps1")

$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallDir = "C:\Program Files\PcHwCtl"
$DataDir = "C:\ProgramData\PcHwCtl"
$ServiceExe = Join-Path $InstallDir "pchwctl.exe"
$SourceConfigFile = Join-Path $RepoRoot "config\pchwctl.json"
$TargetConfigFile = Join-Path $DataDir "pchwctl.json"
$LogFile = Join-Path $DataDir "log.txt"

# 0. This script, the sources and publish\ become SYSTEM's code: nobody but admins and you may be able to change them.
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$sourceWriters = foreach ($dir in @($RepoRoot, $PSScriptRoot, (Join-Path $RepoRoot "src"), (Join-Path $RepoRoot "config"), (Join-Path $RepoRoot "models"), (Join-Path $RepoRoot "publish"))) {
    if (Test-Path -LiteralPath $dir) { Get-NonAdminWriters -Path $dir -AlsoAllowed @($me) | ForEach-Object { "$dir`: $_" } }
}
if (@($sourceWriters).Count -gt 0) {
    throw "Refusing to install: other accounts can change what this installs as SYSTEM:`n  $($sourceWriters -join "`n  ")`nRemove their write access (see docs/install.md) and run again."
}

# 1. Unless -SkipPublish, run publish.ps1 into an empty publish\ (nothing left over from before is deployed)
if (-not $SkipPublish) {
    Remove-TreeSafely -Path (Join-Path $RepoRoot "publish")
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

# 3. Replace C:\Program Files\PcHwCtl\ with publish\*: emptied first, so a DLL someone planted there doesn't survive
$publishDir = Join-Path $RepoRoot "publish"
if (-not (Test-Path -LiteralPath $publishDir)) {
    throw "Publish directory does not exist: $publishDir. Run publish.ps1 or omit -SkipPublish."
}

if (Test-Path -LiteralPath $InstallDir) {
    Assert-NoReparsePoints -Path $InstallDir
    Write-Host "Emptying install directory: $InstallDir"
    Get-ChildItem -LiteralPath $InstallDir -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
} else {
    Write-Host "Creating install directory: $InstallDir"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Set-AdminOnlyAcl -Path $InstallDir -UsersRead

Write-Host "Copying files from $publishDir to $InstallDir..."
Copy-Item -Path (Join-Path $publishDir "*") -Destination $InstallDir -Recurse -Force

# 4. Data directory: secured before anything is written to it. Files a non-admin owns could be planted (a config
# pointing SYSTEM at a DLL, a lasso profile), so they stop the install until someone has looked at them.
if (Test-Path -LiteralPath $DataDir) {
    Assert-NoReparsePoints -Path $DataDir
    $foreign = @(Get-ForeignOwned -Path $DataDir)
    if ($foreign.Count -gt 0 -and -not $TrustExistingData) {
        throw "Refusing to adopt $DataDir`: these are owned by a non-admin account and could have been planted:`n  $($foreign -join "`n  ")`nCheck them (delete what you don't recognise), then run again with -TrustExistingData."
    }
} else {
    Write-Host "Creating data directory: $DataDir"
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
}
Set-AdminOnlyAcl -Path $DataDir

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

Write-Host "Restricting $InstallDir and $DataDir to SYSTEM and Administrators (Users read the install and pchwctl.json)..."
Set-AdminOnlyAcl -Path $InstallDir -UsersRead
Set-AdminOnlyAcl -Path $DataDir -UsersReadFiles @("pchwctl.json")

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
