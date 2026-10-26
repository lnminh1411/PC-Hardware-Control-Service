#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$RestoreFanControl,
    [switch]$RestoreOpenRGB,
    [switch]$RemoveData
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

$InstallDir = "C:\Program Files\PcHwCtl"
$DataDir = "C:\ProgramData\PcHwCtl"

# 1. Stop and delete service PcHwCtl (hands fans back to BIOS control on stop)
$svc = Get-Service -Name "PcHwCtl" -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping service PcHwCtl..."
        Stop-Service -Name "PcHwCtl" -Force
        $timeout = [System.TimeSpan]::FromSeconds(15)
        $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $timeout)
        Write-Host "Service PcHwCtl stopped."
    }
    Write-Host "Deleting service PcHwCtl..."
    Invoke-NativeCommand -FilePath "sc.exe" -ArgumentList @("delete", "PcHwCtl")
    Write-Host "Service PcHwCtl deleted."
} else {
    Write-Host "Service PcHwCtl is not installed."
}

# 2. Remove C:\Program Files\PcHwCtl
if (Test-Path -LiteralPath $InstallDir) {
    Write-Host "Removing installation directory: $InstallDir"
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}

# 3. Handle data directory: keep C:\ProgramData\PcHwCtl unless -RemoveData
if ($RemoveData) {
    if (Test-Path -LiteralPath $DataDir) {
        Write-Host "Removing data directory: $DataDir (-RemoveData specified)"
        Remove-Item -LiteralPath $DataDir -Recurse -Force
    }
} else {
    Write-Host "Retaining data directory at $DataDir (use -RemoveData to delete)."
}

# 4. -RestoreFanControl
if ($RestoreFanControl) {
    Write-Host "Processing -RestoreFanControl..."

    # Registry Run keys (HKCU and HKLM)
    $runKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
    )
    foreach ($rk in $runKeys) {
        if (Test-Path -LiteralPath $rk) {
            $regKey = Get-Item -LiteralPath $rk
            foreach ($valName in $regKey.GetValueNames()) {
                if ($valName -like "*FanControl*.disabled-by-pchwctl") {
                    $originalName = $valName -replace '\.disabled-by-pchwctl$', ''
                    $val = $regKey.GetValue($valName)
                    Write-Host "Restoring registry value in $rk from '$valName' to '$originalName'"
                    Set-ItemProperty -LiteralPath $rk -Name $originalName -Value $val
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
    foreach ($folder in $startupFolders) {
        if (Test-Path -LiteralPath $folder) {
            $files = Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue
            foreach ($file in $files) {
                if ($file.Name -like "*FanControl*.disabled-by-pchwctl") {
                    $originalPath = $file.FullName -replace '\.disabled-by-pchwctl$', ''
                    Write-Host "Restoring startup file from '$($file.FullName)' to '$originalPath'"
                    Move-Item -LiteralPath $file.FullName -Destination $originalPath -Force
                }
            }
        }
    }

    # Enable scheduled tasks whose action path contains FanControl.exe
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
            Write-Host "Enabling scheduled task: $($task.TaskPath)$($task.TaskName)"
            Enable-ScheduledTask -InputObject $task | Out-Null
        }
    }
}

# 5. -RestoreOpenRGB
if ($RestoreOpenRGB) {
    Write-Host "Processing -RestoreOpenRGB..."

    # Registry Run keys (HKCU and HKLM)
    $runKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
    )
    foreach ($rk in $runKeys) {
        if (Test-Path -LiteralPath $rk) {
            $regKey = Get-Item -LiteralPath $rk
            foreach ($valName in $regKey.GetValueNames()) {
                if ($valName -like "*OpenRGB*.disabled-by-pchwctl") {
                    $originalName = $valName -replace '\.disabled-by-pchwctl$', ''
                    $val = $regKey.GetValue($valName)
                    Write-Host "Restoring registry value in $rk from '$valName' to '$originalName'"
                    Set-ItemProperty -LiteralPath $rk -Name $originalName -Value $val
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
    foreach ($folder in $startupFolders) {
        if (Test-Path -LiteralPath $folder) {
            $files = Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue
            foreach ($file in $files) {
                if ($file.Name -like "*OpenRGB*.disabled-by-pchwctl") {
                    $originalPath = $file.FullName -replace '\.disabled-by-pchwctl$', ''
                    Write-Host "Restoring startup file from '$($file.FullName)' to '$originalPath'"
                    Move-Item -LiteralPath $file.FullName -Destination $originalPath -Force
                }
            }
        }
    }

    # Enable scheduled tasks whose action path contains OpenRGB.exe
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
            Write-Host "Enabling scheduled task: $($task.TaskPath)$($task.TaskName)"
            Enable-ScheduledTask -InputObject $task | Out-Null
        }
    }

    # Restore OpenRGB Windows services to Automatic and start them
    $services = Get-CimInstance -ClassName Win32_Service -Filter "PathName LIKE '%OpenRGB.exe%'" -ErrorAction SilentlyContinue
    if ($services) {
        foreach ($s in $services) {
            Write-Host "Restoring service $($s.Name) startup type to Automatic and starting..."
            Set-Service -Name $s.Name -StartupType Automatic
            Start-Service -Name $s.Name -ErrorAction SilentlyContinue
            Write-Host "Service $($s.Name) restored and started."
        }
    }
}

Write-Host "`nUninstallation finished successfully."
