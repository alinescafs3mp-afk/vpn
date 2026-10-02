using AutoVpn.Domain;
using AutoVpn.Infrastructure.Persistence;

namespace AutoVpn.Infrastructure.Broker;

/// <summary>
/// Does not install WFP filters, TUN adapters, routes, or DNS changes.
/// Windows filter behavior is unvalidated, so the production guard refuses
/// instead of applying an untested block-all policy.
/// </summary>
public sealed class UnavailableNetworkGuard : INetworkGuard
{
    public GuardResult Arm(GuardRequest request)
    {
        _ = request;
        return new GuardResult(false, false, PlatformReason(), []);
    }

    public GuardResult Disarm(long generation)
    {
        _ = generation;
        return new GuardResult(true, false, null, []);
    }

    public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
    {
        if (effects.Count == 0)
        {
            return new GuardResult(true, false, null, []);
        }

        return new GuardResult(false, false, PlatformReason(), []);
    }

    public static string PlatformReason()
    {
        return OperatingSystem.IsWindows() ? ReasonCodes.WindowsNotValidated : ReasonCodes.NotWindows;
    }
}
