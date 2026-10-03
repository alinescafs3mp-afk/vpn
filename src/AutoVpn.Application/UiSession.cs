using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Application;

public sealed record UiSession
{
    public string PhaseCode { get; init; } = "Unknown";
    public string PhaseLabel { get; init; } = "Состояние неизвестно";
    public string Detail { get; init; } = "Служба ещё не вызывалась. Доступность серверов не измерялась.";
    public bool LastKnownProtectionArmed { get; init; }
    public bool BrokerReachable { get; init; }
    public bool ClaimsVerifiedDisconnect { get; init; }
    public bool DisclosureAccepted { get; init; }
    public bool SafetyDisconnectAvailable { get; init; }
    public string PrimaryAction { get; init; } = Ru.Connect;
    public long StateRevision { get; init; }
}

public readonly record struct ExitDecision(bool CanClose, string? Reason);

public static class UiSessionReducer
{
    public static UiSession Initial()
    {
        return new UiSession();
    }

    public static UiSession BrokerUnreachable(UiSession previous)
    {
        var armed = previous.LastKnownProtectionArmed;
        var safety = armed || SessionText.OffersDisconnect(previous.PhaseCode, protectionArmed: false);
        var detail = armed
            ? "Брокер недоступен. Последний известный признак защиты: включена. Отключение не подтверждено."
            : "Брокер недоступен. Последний известный признак защиты: не включена. Отключение не подтверждено.";
        return previous with
        {
            PhaseCode = "Unknown",
            PhaseLabel = "Состояние неизвестно",
            Detail = detail,
            BrokerReachable = false,
            ClaimsVerifiedDisconnect = false,
            SafetyDisconnectAvailable = safety,
            PrimaryAction = safety ? Ru.Disconnect : Ru.Connect,
        };
    }

    public static UiSession FromSnapshot(UiSession previous, BrokerSnapshot snapshot, string? message, bool disclosureAccepted)
    {
        var safety = SessionText.OffersDisconnect(snapshot.Phase, snapshot.ProtectionArmed);
        var verifiedDisconnect = snapshot.Phase == nameof(TunnelPhase.Disconnected) && !snapshot.ProtectionArmed;
        return previous with
        {
            PhaseCode = snapshot.Phase,
            PhaseLabel = SessionText.Phase(snapshot.Phase),
            Detail = message ?? previous.Detail,
            LastKnownProtectionArmed = snapshot.ProtectionArmed,
            BrokerReachable = true,
            ClaimsVerifiedDisconnect = verifiedDisconnect,
            DisclosureAccepted = disclosureAccepted,
            SafetyDisconnectAvailable = safety,
            PrimaryAction = safety ? Ru.Disconnect : Ru.Connect,
            StateRevision = snapshot.Revision,
        };
    }

    public static bool ConnectAllowed(bool disclosureAccepted, bool hasMeasuredEligible)
    {
        return disclosureAccepted && hasMeasuredEligible;
    }

    public static ExitDecision PlanExit(UiSession session, bool operationPending = false)
    {
        if (operationPending && !session.ClaimsVerifiedDisconnect)
        {
            return new ExitDecision(false, "Выход остановлен: операция ещё не завершена. Нужно подтверждённое отключение.");
        }

        if (session.ClaimsVerifiedDisconnect)
        {
            return new ExitDecision(true, null);
        }

        if (!session.BrokerReachable && (session.LastKnownProtectionArmed || session.SafetyDisconnectAvailable))
        {
            return new ExitDecision(false, "Выход остановлен: брокер не подтвердил отключение.");
        }

        if (session.SafetyDisconnectAvailable)
        {
            return new ExitDecision(false, "Выход остановлен: сначала нужно подтверждённое отключение.");
        }

        if (!session.BrokerReachable && session.PhaseCode == "Unknown" && !session.LastKnownProtectionArmed)
        {
            return new ExitDecision(true, null);
        }

        return new ExitDecision(true, null);
    }
}

public sealed class UiOperationLease
{
    private int _generation;
    private bool _pending;

    public bool Pending => _pending;

    public int Start()
    {
        var generation = Interlocked.Increment(ref _generation);
        _pending = true;
        return generation;
    }

    public int Supersede()
    {
        return Interlocked.Increment(ref _generation);
    }

    public bool Owns(int generation)
    {
        return generation == Volatile.Read(ref _generation);
    }

    public bool FinishIfCurrent(int generation)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return false;
        }

        _pending = false;
        return true;
    }
}

public sealed class SessionMailbox
{
    public UiSession Session { get; private set; } = UiSessionReducer.Initial();

    public string? BootId { get; private set; }

    private readonly HashSet<string> _retiredBoots = new(StringComparer.Ordinal);

    public long Sequence { get; private set; } = -1;

    public string? LastErrorCode { get; private set; }

    public bool OperationPending { get; set; }

    public void NoteDisclosure(bool accepted)
    {
        Session = Session with { DisclosureAccepted = accepted };
    }

    public void ApplyTransportLoss()
    {
        LastErrorCode = null;
        Session = UiSessionReducer.BrokerUnreachable(Session);
    }

    public bool Apply(IpcResponse response, bool disclosureAccepted)
    {
        if (response.Snapshot is null)
        {
            LastErrorCode = string.IsNullOrWhiteSpace(response.ErrorCode) ? "NO_SNAPSHOT" : response.ErrorCode;
            var message = string.IsNullOrWhiteSpace(response.Message) ? "Ответ без снимка состояния." : response.Message;
            Session = Session with
            {
                Detail = LastErrorCode + ": " + message,
                BrokerReachable = false,
                ClaimsVerifiedDisconnect = false,
                DisclosureAccepted = disclosureAccepted,
            };
            return false;
        }

        var snapshot = response.Snapshot;
        if (BootId is not null && string.Equals(BootId, snapshot.BootId, StringComparison.Ordinal))
        {
            if (snapshot.Sequence < Sequence)
            {
                return false;
            }

            if (snapshot.Sequence == Sequence && snapshot.Revision < Session.StateRevision)
            {
                return false;
            }
        }
        else if (BootId is not null)
        {
            if (snapshot.BootId is null || _retiredBoots.Contains(snapshot.BootId))
            {
                return false;
            }

            _retiredBoots.Add(BootId);
        }

        BootId = snapshot.BootId;
        Sequence = snapshot.Sequence;
        Session = UiSessionReducer.FromSnapshot(Session, snapshot, response.Message, disclosureAccepted);
        if (!string.IsNullOrWhiteSpace(response.ErrorCode))
        {
            LastErrorCode = response.ErrorCode;
            Session = Session with { Detail = response.ErrorCode + ": " + (response.Message ?? Session.Detail) };
        }
        else
        {
            LastErrorCode = null;
        }

        return true;
    }
}

public static class Consent
{
    public static void AcceptDisclosure(ICatalogue catalogue)
    {
        if (catalogue.Settings.DisclosureAccepted)
        {
            return;
        }

        catalogue.Settings = catalogue.Settings with
        {
            DisclosureAccepted = true,
            Revision = catalogue.Settings.Revision + 1,
        };
    }
}

public static class CataloguePresentation
{
    public static string Servers(ICatalogue catalogue, string filter, DateTimeOffset nowUtc)
    {
        IEnumerable<CatalogueNode> selected = catalogue.Nodes;
        if (filter == "favorites")
        {
            selected = catalogue.Nodes.Where(node => node.Favorite);
        }
        else if (filter == "working")
        {
            var eligible = catalogue.Eligible(new EligibilityContext
            {
                NowUtc = nowUtc,
                NetworkEpoch = catalogue.NetworkEpoch,
                AllowInsecureCertificates = catalogue.Settings.AllowInsecureCertificates,
            }).Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
            selected = catalogue.Nodes.Where(node => eligible.Contains(node.NodeId));
        }

        var nodes = selected.OrderBy(node => node.Label, StringComparer.Ordinal).ToArray();
        if (nodes.Length == 0)
        {
            return filter switch
            {
                "working" => "Рабочих серверов нет. Проверка не отмечала их как доступные.",
                "favorites" => "Избранное пусто.",
                _ => "Каталог пуст. Названия и страны из подписки не считаются доказательством доступности.",
            };
        }

        return string.Join('\n', nodes.Select(Format));
    }

    public static string Format(CatalogueNode node)
    {
        var latency = node.Assessment?.MedianLatencyMs is int ms ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture) + " мс" : "не измерялась";
        var country = string.IsNullOrWhiteSpace(node.AdvertisedCountry) ? "не указана" : node.AdvertisedCountry;
        var health = node.Assessment?.Health switch
        {
            HealthState.Healthy => Ru.Working,
            HealthState.Degraded => Ru.Working,
            HealthState.Failed => Ru.Unavailable,
            HealthState.EnvironmentUnknown => "Среда проверки не подтвердила сервер",
            _ => Ru.NeedsCheck,
        };
        return node.Label + " — " + health + ". Задержка: " + latency + ". Страна по подписке: " + country + ".";
    }
}
