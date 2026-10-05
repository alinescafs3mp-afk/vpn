using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.WindowsService;

/// <summary>Allows only the configured owner to identify and wait on this service process.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceProcessQueryAccess
{
    // No VM read/write, handle duplication, terminate, or security-descriptor mutation rights.
    public const uint RequiredAccess = 0x00101000; // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION

    public static void GrantToOwner(string ownerSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18") throw new UnauthorizedAccessException("SYSTEM_REQUIRED");
        var process = GetCurrentProcess(); // Only our own process, never a caller-supplied PID.
        var error = GetSecurityInfo(process, 6, 4, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception(checked((int)error));
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 65536) throw new InvalidDataException("PROCESS_DESCRIPTOR_SIZE");
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var acl = BuildQueryAcl(bytes, ownerSid);
            error = SetSecurityInfo(process, 6, 4, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero);
            if (error != 0) throw new Win32Exception(checked((int)error));
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    /// <summary>Preserves every original ACE; adds one non-inheritable, read-only allow ACE.</summary>
    public static byte[] BuildQueryAcl(byte[] descriptor, string ownerSid)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!ServiceOwnerConfiguration.IsAccountSid(ownerSid)) throw new InvalidDataException("SERVICE_OWNER_INVALID");
        if (descriptor.Length is 0 or > 65536) throw new InvalidDataException("PROCESS_DESCRIPTOR_SIZE");
        var security = new RawSecurityDescriptor(descriptor, 0);
        var acl = security.DiscretionaryAcl;
        if ((security.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || acl is null)
            throw new InvalidDataException("PROCESS_NULL_DACL");
        var sid = new SecurityIdentifier(ownerSid);
        var covered = false;
        var insertion = acl.Count;
        for (var i = 0; i < acl.Count; i++)
        {
            var ace = acl[i];
            if ((ace.AceFlags & AceFlags.Inherited) != 0 && insertion == acl.Count) insertion = i;
            if (ace is CommonAce allow && !allow.IsCallback && allow.AceFlags == AceFlags.None &&
                allow.AceQualifier == AceQualifier.AccessAllowed && allow.SecurityIdentifier == sid &&
                (unchecked((uint)allow.AccessMask) & RequiredAccess) == RequiredAccess) covered = true;
        }
        // Explicit denies remain effective. Do not rewrite unrelated grants or inherited entries.
        if (!covered) acl.InsertAce(insertion, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
            checked((int)RequiredAccess), sid, false, null));
        var result = new byte[acl.BinaryLength];
        acl.GetBinaryForm(result, 0);
        return result;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
