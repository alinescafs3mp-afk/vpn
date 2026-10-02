namespace AutoVpn.Domain;

public static class SessionText
{
    public static string Phase(string? phase)
    {
        return phase switch
        {
            nameof(TunnelPhase.Connected) => Ru.Connected,
            nameof(TunnelPhase.Connecting) or nameof(TunnelPhase.PreparingProtection) => Ru.Connecting,
            nameof(TunnelPhase.Reconnecting) => Ru.Reconnecting,
            nameof(TunnelPhase.Blocked) => "Заблокировано",
            nameof(TunnelPhase.RestoringNetwork) => "Восстановление сети",
            _ => Ru.Disconnected,
        };
    }

    public static bool IsConnected(string? phase)
    {
        return phase == nameof(TunnelPhase.Connected);
    }
}
