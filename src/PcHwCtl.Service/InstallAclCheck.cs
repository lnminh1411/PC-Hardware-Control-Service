using System.Security.AccessControl;
using System.Security.Principal;

namespace PcHwCtl.Service;

/// <summary>
/// At service start: warns when someone other than SYSTEM and Administrators can change what this SYSTEM process
/// loads (its install directory, exe, dll) or reads as config (the data directory). scripts/install.ps1 sets these;
/// an install that predates it, or a hand copy, leaves the installing user with Full control.
/// </summary>
public static class InstallAclCheck
{
    private const FileSystemRights Write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.CreatorOwnerSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    ];

    public static void Warn(string dataDir, Action<string> log)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var paths = new List<(string Path, bool Dir)>
        {
            (installDir, true),
            (Path.Combine(installDir, "pchwctl.exe"), false),
            (Path.Combine(installDir, "pchwctl.dll"), false),
            (dataDir, true),
            (Path.Combine(dataDir, "pchwctl.json"), false),
        };
        foreach (var (path, dir) in paths)
        {
            try
            {
                if (dir ? !Directory.Exists(path) : !File.Exists(path)) continue;
                FileSystemSecurity acl = dir ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                var writers = Writers(acl);
                if (writers.Count > 0)
                    log($"SECURITY WARNING: {string.Join(", ", writers)} can change {path}, which this SYSTEM service loads. Run scripts\\install.ps1 elevated to fix.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                log($"Could not check the permissions of {path}: {ex.Message}");
            }
        }
    }

    private static List<string> Writers(FileSystemSecurity acl)
    {
        var writers = new List<string>();
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner != null && !Trusted.Contains(owner)) writers.Add($"{Name(owner)} (owner)");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & Write) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && !Trusted.Contains(sid)) writers.Add(Name(sid));
        }
        return writers.Distinct().ToList();
    }

    private static string Name(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
