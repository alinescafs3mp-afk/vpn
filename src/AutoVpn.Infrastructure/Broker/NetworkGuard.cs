namespace AutoVpn.Infrastructure.Broker;

public sealed record GuardRequest(long Generation, bool LanAccess, bool ProtectionRequired);

public sealed record GuardResult(bool Completed, bool Armed, string? ReasonCode, IReadOnlyList<string> RemovedIds);

public interface INetworkGuard
{
    GuardResult Arm(GuardRequest request);

    GuardResult Disarm(long generation);

    GuardResult Recover(IReadOnlyList<Persistence.OwnedEffect> effects);
}
