using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AutoVpn.Infrastructure.WindowsService;

namespace AutoVpn.UnitTests;

[SupportedOSPlatform("windows")]
public sealed class AstraV3FServiceAccessTests
{
    private const string Owner = "S-1-5-21-111-222-333-1001";
    private const string Other = "S-1-5-21-111-222-333-1002";

    [WindowsHandleFact]
    public void OnlyOwnerQueryAndSynchronizeAreAdded()
    {
        var original = Descriptor("D:(A;;GA;;;SY)(A;;GA;;;BA)");
        var before = new RawSecurityDescriptor(original, 0).DiscretionaryAcl!;
        var after = new RawAcl(ServiceProcessQueryAccess.BuildQueryAcl(original, Owner), 0);
        Assert.Equal(before.Count + 1, after.Count);
        for (var i = 0; i < before.Count; i++) Assert.Equal(Bytes(before[i]), Bytes(after[i]));
        var added = Assert.IsType<CommonAce>(after[after.Count - 1]);
        Assert.Equal(new SecurityIdentifier(Owner), added.SecurityIdentifier);
        Assert.Equal(0x00101000, added.AccessMask);
        Assert.Equal(AceFlags.None, added.AceFlags);
        Assert.Equal(AceQualifier.AccessAllowed, added.AceQualifier);
        Assert.False(added.IsCallback);
    }

    [WindowsHandleFact]
    public void ExistingOwnerGrantIsIdempotent()
    {
        var once = ServiceProcessQueryAccess.BuildQueryAcl(Descriptor("D:(A;;GA;;;SY)"), Owner);
        var security = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
            null, null, null, new RawAcl(once, 0));
        Assert.Equal(once, ServiceProcessQueryAccess.BuildQueryAcl(Bytes(security), Owner));
    }

    [WindowsHandleFact]
    public void ExplicitDenialIsPreservedBeforeOwnerAllow()
    {
        var original = Descriptor($"D:(D;;0x1000;;;{Owner})(A;;GA;;;SY)");
        var before = new RawSecurityDescriptor(original, 0).DiscretionaryAcl!;
        var after = new RawAcl(ServiceProcessQueryAccess.BuildQueryAcl(original, Owner), 0);
        Assert.Equal(Bytes(before[0]), Bytes(after[0]));
        Assert.Equal(AceQualifier.AccessDenied, Assert.IsType<CommonAce>(after[0]).AceQualifier);
    }

    [WindowsHandleFact]
    public void InheritedRulesAndAnotherUserAreNotReplaced()
    {
        var original = Descriptor($"D:(A;;GA;;;SY)(A;ID;0x1000;;;{Other})");
        var before = new RawSecurityDescriptor(original, 0).DiscretionaryAcl!;
        var after = new RawAcl(ServiceProcessQueryAccess.BuildQueryAcl(original, Owner), 0);
        Assert.Equal(Bytes(before[0]), Bytes(after[0]));
        Assert.Equal(Owner, Assert.IsType<CommonAce>(after[1]).SecurityIdentifier.Value);
        Assert.Equal(Bytes(before[1]), Bytes(after[2]));
    }

    [WindowsHandleFact]
    public void NullDaclFailsClosed()
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.SelfRelative, null, null, null, null);
        Assert.Throws<InvalidDataException>(() => ServiceProcessQueryAccess.BuildQueryAcl(Bytes(descriptor), Owner));
    }

    [WindowsHandleFact]
    public void WellKnownAndMalformedOwnersCannotReceiveAGrant()
    {
        foreach (var owner in new[] { "", "S-1-1-0", "S-1-5-18", "S-1-5-32-545", "not-a-sid" })
            Assert.Throws<InvalidDataException>(() => ServiceProcessQueryAccess.BuildQueryAcl(Descriptor("D:(A;;GA;;;SY)"), owner));
    }

    private static byte[] Descriptor(string sddl) => Bytes(new RawSecurityDescriptor(sddl));
    private static byte[] Bytes(GenericSecurityDescriptor descriptor)
    { var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes; }
    private static byte[] Bytes(GenericAce ace)
    { var bytes = new byte[ace.BinaryLength]; ace.GetBinaryForm(bytes, 0); return bytes; }
}
