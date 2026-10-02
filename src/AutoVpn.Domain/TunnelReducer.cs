namespace AutoVpn.Domain;

public enum TunnelPhase
{
    Disconnected = 0,
    PreparingProtection = 1,
    Connecting = 2,
    Connected = 3,
    Reconnecting = 4,
    Blocked = 5,
    RestoringNetwork = 6,
}

public enum TunnelCommandKind
{
    Connect = 0,
    ProtectionArmed = 1,
    ProtectionFailed = 2,
    CoreStarted = 3,
    ProductionVerified = 4,
    VerifyFailed = 5,
    HealthFailed = 6,
    SwitchCommitted = 7,
    Disconnect = 8,
    RestoreFinished = 9,
    CoreExited = 10,
    UplinkLost = 11,
    Block = 12,
    ProtectionReleased = 13,
}

public sealed record TunnelState
{
    public TunnelPhase Phase { get; init; } = TunnelPhase.Disconnected;
    public long Generation { get; init; } = 1;
    public long Revision { get; init; }
    public string? ActiveNodeId { get; init; }
    public bool ProtectionArmed { get; init; }
    public string? BlockReason { get; init; }
    public bool DisconnectCommitted { get; init; }

    public static TunnelState Initial { get; } = new();
}

public sealed record TunnelCommand(TunnelCommandKind Kind, long Generation, string? NodeId = null, string? Reason = null);

public static class TunnelReducer
{
    public static TunnelState Apply(TunnelState state, TunnelCommand command)
    {
        if (command.Kind != TunnelCommandKind.Disconnect && command.Generation != state.Generation)
        {
            return state;
        }

        if (state.DisconnectCommitted && command.Kind != TunnelCommandKind.RestoreFinished &&
            command.Generation == state.Generation)
        {
            return state;
        }

        return command.Kind switch
        {
            TunnelCommandKind.Disconnect => state with
            {
                Phase = TunnelPhase.RestoringNetwork,
                Generation = state.Generation + 1,
                Revision = state.Revision + 1,
                ProtectionArmed = state.ProtectionArmed,
                DisconnectCommitted = true,
                BlockReason = null,
            },
            TunnelCommandKind.Connect when state.Phase is TunnelPhase.Disconnected or TunnelPhase.Blocked => state with
            {
                Phase = TunnelPhase.PreparingProtection,
                Revision = state.Revision + 1,
                ActiveNodeId = command.NodeId,
                DisconnectCommitted = false,
                BlockReason = null,
            },
            TunnelCommandKind.ProtectionArmed when state.Phase == TunnelPhase.PreparingProtection => state with
            {
                Phase = TunnelPhase.Connecting,
                Revision = state.Revision + 1,
                ProtectionArmed = true,
            },
            TunnelCommandKind.ProtectionFailed when state.Phase == TunnelPhase.PreparingProtection => state with
            {
                Phase = TunnelPhase.Blocked,
                Revision = state.Revision + 1,
                ProtectionArmed = false,
                BlockReason = command.Reason ?? ReasonCodes.WindowsNotValidated,
            },
            TunnelCommandKind.CoreStarted when state.Phase == TunnelPhase.Connecting => state,
            TunnelCommandKind.ProductionVerified when state.Phase is TunnelPhase.Connecting or TunnelPhase.Reconnecting => state with
            {
                Phase = TunnelPhase.Connected,
                Revision = state.Revision + 1,
                ActiveNodeId = command.NodeId ?? state.ActiveNodeId,
                BlockReason = null,
            },
            TunnelCommandKind.VerifyFailed when state.Phase is TunnelPhase.Connecting or TunnelPhase.Reconnecting => state with
            {
                Phase = TunnelPhase.Blocked,
                Revision = state.Revision + 1,
                ProtectionArmed = true,
                BlockReason = command.Reason ?? ReasonCodes.ProbeFailed,
            },
            TunnelCommandKind.HealthFailed or TunnelCommandKind.CoreExited when state.Phase == TunnelPhase.Connected => state with
            {
                Phase = TunnelPhase.Reconnecting,
                Revision = state.Revision + 1,
                ProtectionArmed = true,
                BlockReason = command.Reason,
            },
            TunnelCommandKind.UplinkLost when state.Phase is TunnelPhase.Connected or TunnelPhase.Reconnecting => state with
            {
                Phase = TunnelPhase.Blocked,
                Revision = state.Revision + 1,
                ProtectionArmed = true,
                BlockReason = ReasonCodes.UplinkOffline,
            },
            TunnelCommandKind.Block when state.Phase is TunnelPhase.Connected or TunnelPhase.Reconnecting or TunnelPhase.Connecting => state with
            {
                Phase = TunnelPhase.Blocked,
                Revision = state.Revision + 1,
                ProtectionArmed = state.ProtectionArmed,
                BlockReason = command.Reason,
            },
            TunnelCommandKind.SwitchCommitted when state.Phase == TunnelPhase.Reconnecting => state with
            {
                Phase = TunnelPhase.Connecting,
                Revision = state.Revision + 1,
                ActiveNodeId = command.NodeId,
            },
            TunnelCommandKind.RestoreFinished when state.Phase == TunnelPhase.RestoringNetwork => new TunnelState
            {
                Phase = TunnelPhase.Disconnected,
                Generation = state.Generation,
                Revision = state.Revision + 1,
                ProtectionArmed = false,
                DisconnectCommitted = false,
            },
            TunnelCommandKind.ProtectionReleased when state.Phase == TunnelPhase.Blocked && state.ProtectionArmed => state with
            {
                Revision = state.Revision + 1,
                ProtectionArmed = false,
            },
            _ => state,
        };
    }
}
