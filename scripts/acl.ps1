# Shared by install.ps1 and uninstall.ps1 (dot-sourced): the checks that keep an elevated install from being turned
# against the machine. Whoever can write what SYSTEM loads, or what an elevated script walks, can become SYSTEM.

$script:SidAdmins = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
$script:SidSystem = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
$script:SidUsers = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-545")
$script:SidTrustedInstaller = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
$script:SidCreatorOwner = New-Object System.Security.Principal.SecurityIdentifier("S-1-3-0")

# Rights that let a principal change a file or what a directory holds.
$script:WriteRights = [System.Security.AccessControl.FileSystemRights]"WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership"

# Every reparse point (junction, symlink, mount point) under Path, not descending into any. An elevated icacls /T or
# Remove-Item -Recurse follows them, so a non-admin's junction would aim it at a tree of their choosing.
function Get-ReparsePoints {
    param([Parameter(Mandatory = $true)][string]$Path)
    $found = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
    $root = New-Object System.IO.DirectoryInfo($Path)
    if (-not $root.Exists) { return $found }
    if ($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { $found.Add($root.FullName); return $found }
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        foreach ($item in $dir.EnumerateFileSystemInfos()) {
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { $found.Add($item.FullName); continue }
            if ($item -is [System.IO.DirectoryInfo]) { $stack.Push($item) }
        }
    }
    return $found
}

function Assert-NoReparsePoints {
    param([Parameter(Mandatory = $true)][string]$Path)
    $points = @(Get-ReparsePoints -Path $Path)
    if ($points.Count -gt 0) {
        throw "Refusing to touch $Path`: it contains junctions or symlinks, which an elevated script would follow:`n  $($points -join "`n  ")`nRemove them (rmdir <path>, not rmdir /s) and run again."
    }
}

# The non-admin principals that may write Path (its own ACL; no recursion). $AlsoAllowed: SIDs that are fine too.
function Get-NonAdminWriters {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [System.Security.Principal.SecurityIdentifier[]]$AlsoAllowed = @()
    )
    $trusted = @($script:SidAdmins, $script:SidSystem, $script:SidTrustedInstaller, $script:SidCreatorOwner) + $AlsoAllowed
    $acl = Get-Acl -LiteralPath $Path
    $writers = foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (($rule.FileSystemRights -band $script:WriteRights) -eq 0) { continue }
        if ($trusted -contains $rule.IdentityReference) { continue }
        try { $rule.IdentityReference.Translate([System.Security.Principal.NTAccount]).Value } catch { $rule.IdentityReference.Value }
    }
    return @($writers | Select-Object -Unique)
}

# Items under Path owned by someone other than SYSTEM, Administrators or TrustedInstaller: what a non-admin put there.
function Get-ForeignOwned {
    param([Parameter(Mandatory = $true)][string]$Path)
    $trusted = @($script:SidAdmins, $script:SidSystem, $script:SidTrustedInstaller)
    $items = @(Get-Item -LiteralPath $Path -Force) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue)
    foreach ($item in $items) {
        $owner = (Get-Acl -LiteralPath $item.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
        if ($trusted -notcontains $owner) {
            $name = try { $owner.Translate([System.Security.Principal.NTAccount]).Value } catch { $owner.Value }
            "$($item.FullName) (owner $name)"
        }
    }
}

# SYSTEM and Administrators only, owned by Administrators. -UsersRead: Users may read and run everything (the install
# directory: the agent runs from it). Otherwise Users may only list the directory and read the files in -UsersReadFiles
# (the data directory: the agent reads pchwctl.json; logs, state and the screenshot deletes stay private).
function Set-AdminOnlyAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$UsersRead,
        [string[]]$UsersReadFiles = @()
    )

    Assert-NoReparsePoints -Path $Path
    $inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    $noInherit = [System.Security.AccessControl.InheritanceFlags]::None
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow

    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetOwner($script:SidAdmins)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($script:SidSystem, "FullControl", $inherit, $none, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($script:SidAdmins, "FullControl", $inherit, $none, $allow)))
    if ($UsersRead) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($script:SidUsers, "ReadAndExecute", $inherit, $none, $allow)))
    } else {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($script:SidUsers, "ReadAndExecute", $noInherit, $none, $allow)))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl

    # Everything below drops its own entries and inherits the above (no junctions: checked first).
    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
        Invoke-NativeCommand -FilePath "icacls.exe" -ArgumentList @((Join-Path $Path "*"), "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q") | Out-Null
        Invoke-NativeCommand -FilePath "icacls.exe" -ArgumentList @((Join-Path $Path "*"), "/reset", "/T", "/C", "/Q") | Out-Null
    }

    foreach ($name in $UsersReadFiles) {
        $file = Join-Path $Path $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $fileAcl = Get-Acl -LiteralPath $file
        $fileAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($script:SidUsers, "Read", $allow)))
        Set-Acl -LiteralPath $file -AclObject $fileAcl
    }
}

# Remove-Item -Recurse on Windows PowerShell 5.1 can follow a junction into its target: check, then delete.
function Remove-TreeSafely {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-NoReparsePoints -Path $Path
    Remove-Item -LiteralPath $Path -Recurse -Force
}
