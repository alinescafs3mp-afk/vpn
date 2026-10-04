using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.WindowsService;

[SupportedOSPlatform("windows")]
public static class InstalledServiceLayout
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AutoVPN");
    public static string DirectoryPath => Path.Combine(Root, "Service");
    public static string ExecutablePath => Path.Combine(DirectoryPath, "AutoVpn.Service.exe");

    public static ServiceOwnerConfiguration LoadOwner()
    {
        using var self = WindowsIdentity.GetCurrent();
        if (self.User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("SYSTEM_REQUIRED");
        if (!string.Equals(Environment.ProcessPath, ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("INSTALLED_PATH_REQUIRED");
        DemandProtectedEntry(new DirectoryInfo(Root));
        var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(DirectoryPath));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            DemandProtectedEntry(directory);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++count > 4096) throw new InvalidDataException("INSTALLATION_TOO_LARGE");
                DemandProtectedEntry(entry);
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
        using var input = new FileStream(Path.Combine(DirectoryPath, "service-owner.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 1024) throw new InvalidDataException("SERVICE_OWNER_SIZE");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return ServiceOwnerConfiguration.Parse(bytes);
    }

    public static void DemandProtectedEntry(FileSystemInfo entry)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("INSTALLATION_LINK_OR_MISSING");
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if ((raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || raw.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("INSTALLATION_NULL_DACL");
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is not ("S-1-5-18" or "S-1-5-32-544")) throw new UnauthorizedAccessException("INSTALLATION_OWNER");
        const FileSystemRights mutations = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var sid = rule.IdentityReference.Value;
            var writes = (rule.FileSystemRights & mutations) != 0 ||
                (unchecked((uint)rule.FileSystemRights) & 0x50000000u) != 0;
            if (writes && sid is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("INSTALLATION_WRITABLE");
        }
    }
}
