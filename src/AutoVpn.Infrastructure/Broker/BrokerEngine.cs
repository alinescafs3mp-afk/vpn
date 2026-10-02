using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Persistence;

namespace AutoVpn.Infrastructure.Broker;

public sealed class BrokerEngine
{
    private readonly ICatalogue _catalogue;
    private readonly INetworkGuard _guard;
    private readonly ICoreController _core;
    private readonly EffectJournal? _journal;
    private readonly object _gate = new();
    private TunnelState _state = TunnelState.Initial;
    private long _sequence;
    private bool _coreRunning;
    private List<StandbyCandidate> _standbys = [];
    private int _switchesInWindow;

    public BrokerEngine(ICatalogue catalogue, INetworkGuard guard, ICoreController core, EffectJournal? journal = null)
    {
        _catalogue = catalogue;
        _guard = guard;
        _core = core;
        _journal = journal;
    }

    public TunnelState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Operation switch
            {
                IpcOperations.GetSnapshot => Ok(request, Snapshot()),
                IpcOperations.Connect => await ConnectAsync(request, cancellationToken).ConfigureAwait(false),
                IpcOperations.Disconnect => await DisconnectAsync(request, cancellationToken).ConfigureAwait(false),
                IpcOperations.ReportHealth => ReportHealth(request),
                IpcOperations.ApplyRuntimeSet => ApplyRuntimeSet(request),
                IpcOperations.RecoverOwned => Recover(request),
                _ => Fail(request, "UNKNOWN_OPERATION", "Операция не разрешена."),
            };
        }
        catch (JsonException)
        {
            return Fail(request, "MALFORMED", "Запрос повреждён.");
        }
    }

    public BrokerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnlocked();
        }
    }

    private BrokerSnapshot SnapshotUnlocked()
    {
        var active = _state.ActiveNodeId is null ? null : _catalogue.Nodes.FirstOrDefault(node => node.NodeId == _state.ActiveNodeId);
        return new BrokerSnapshot
        {
            Revision = _state.Revision,
            Sequence = _sequence,
            Generation = _state.Generation,
            Phase = _state.Phase.ToString(),
            ActiveNodeId = _state.ActiveNodeId,
            ProtectionArmed = _state.ProtectionArmed,
            BlockReason = _state.BlockReason,
            CoreRunning = _coreRunning,
            CoreVersion = ProductLimits.CoreVersion,
            CountryLabel = active?.AdvertisedCountry,
            ServerLabel = active?.Label,
            LatencyMs = active?.Assessment?.MedianLatencyMs,
            LastCheckUtc = active?.Assessment?.LastSuccessUtc,
            StandbyCount = _standbys.Count,
        };
    }

    private async Task<IpcResponse> ConnectAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var payload = request.Payload.ValueKind == JsonValueKind.Undefined
            ? new ConnectPayload { NodeId = "", Digest = "", NetworkEpoch = _catalogue.NetworkEpoch }
            : request.Payload.Deserialize<ConnectPayload>(IpcJson.RequestOptions);
        if (payload is null)
        {
            return Fail(request, "MALFORMED", "Запрос повреждён.");
        }

        if (!_catalogue.Settings.DisclosureAccepted)
        {
            return Fail(request, "DISCLOSURE", "Сначала подтвердите предупреждение о публичных серверах.");
        }

        if (payload.NetworkEpoch != 0 && payload.NetworkEpoch != _catalogue.NetworkEpoch)
        {
            return Fail(request, ReasonCodes.StaleRevision, "Сеть изменилась. Нужна новая проверка серверов.");
        }

        var now = DateTimeOffset.UtcNow;
        var selected = Select(payload, now);
        if (selected is null)
        {
            return Fail(request, ReasonCodes.NoEligibleServer, Ru.NoServer);
        }

        var alreadyRunning = false;
        string? refusal = null;
        lock (_gate)
        {
            if (_state.Phase is TunnelPhase.Connected or TunnelPhase.Connecting or TunnelPhase.PreparingProtection)
            {
                alreadyRunning = true;
            }
            else
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Connect, _state.Generation, selected.NodeId));
                _sequence++;
                var armed = _guard.Arm(new GuardRequest(_state.Generation, _catalogue.Settings.LanAccess, _catalogue.Settings.ProtectionOnConnect));
                if (!armed.Armed)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.ProtectionFailed, _state.Generation, selected.NodeId, armed.ReasonCode));
                    _sequence++;
                    refusal = armed.ReasonCode ?? ReasonCodes.WindowsNotValidated;
                }
                else
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.ProtectionArmed, _state.Generation, selected.NodeId));
                    _sequence++;
                }
            }
        }

        if (alreadyRunning)
        {
            return Ok(request, Snapshot(), "Сессия уже запущена. Повторное подключение её не перезапускает.");
        }

        if (refusal is not null)
        {
            _catalogue.SetActiveNode(null);
            return Fail(request, refusal, BlockMessage(refusal));
        }

        var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = NewSecret(),
            ControllerPort = 12789,
            SocksPort = null,
            Tun = true,
            LanAccess = _catalogue.Settings.LanAccess,
            Nodes = [NodeWireFactory.FromCatalogue(selected)],
            SelectedNodeId = selected.NodeId,
        });
        var started = await _core.StartAsync(yaml, cancellationToken).ConfigureAwait(false);
        string? startRefusal = null;
        lock (_gate)
        {
            if (!started.Started)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, selected.NodeId, started.ReasonCode));
                _sequence++;
                _coreRunning = false;
                startRefusal = started.ReasonCode ?? ReasonCodes.CoreConfigRejected;
            }
            else
            {
                _coreRunning = true;
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.CoreStarted, _state.Generation, selected.NodeId));
                _sequence++;
            }
        }

        if (startRefusal is not null)
        {
            return Fail(request, startRefusal, "Ядро не запущено. Подключение не объявлено.");
        }

        _catalogue.SetActiveNode(selected.NodeId);
        return Ok(request, Snapshot(), "Ядро запущено. Канал ещё не подтверждён, состояние не «подключён».");
    }

    /// <summary>
    /// In-process only. IPC cannot mark the tunnel connected.
    /// </summary>
    public void ConfirmProduction(bool ok, string? reasonCode)
    {
        lock (_gate)
        {
            if (_state.Phase is not (TunnelPhase.Connecting or TunnelPhase.Reconnecting))
            {
                return;
            }

            var command = ok
                ? new TunnelCommand(TunnelCommandKind.ProductionVerified, _state.Generation, _state.ActiveNodeId)
                : new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, _state.ActiveNodeId, reasonCode);
            _state = TunnelReducer.Apply(_state, command);
            _sequence++;
        }
    }

    private async Task<IpcResponse> DisconnectAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Disconnect, _state.Generation));
            _sequence++;
        }

        await _core.StopAsync(cancellationToken).ConfigureAwait(false);
        _coreRunning = false;
        _catalogue.SetActiveNode(null);
        var disarm = _guard.Disarm(_state.Generation);
        var recovery = _journal?.Recover(_guard);
        var unfinished = recovery is { Completed: false } || !disarm.Completed;
        if (unfinished)
        {
            return Fail(request, recovery?.ReasonCode ?? ReasonCodes.WindowsNotValidated,
                "Отключение запрошено, но свои сетевые правила не подтверждены как снятые.");
        }

        lock (_gate)
        {
            _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.RestoreFinished, _state.Generation));
            _sequence++;
        }

        return Ok(request, Snapshot(), "Подключение остановлено.");
    }

    private IpcResponse ReportHealth(IpcRequest request)
    {
        var payload = request.Payload.Deserialize<HealthPayload>(IpcJson.RequestOptions);
        if (payload is null)
        {
            return Fail(request, "MALFORMED", "Запрос повреждён.");
        }

        if (payload.NetworkEpoch != _catalogue.NetworkEpoch)
        {
            return Fail(request, "EPOCH_MISMATCH", "Проверка относится к другой сетевой эпохе и проигнорирована.");
        }

        if (!Enum.TryParse<FailureKind>(payload.FailureKind, ignoreCase: true, out var failure))
        {
            return Fail(request, "MALFORMED", "Неизвестный тип сбоя.");
        }

        var stayed = false;
        lock (_gate)
        {
            var decision = FailoverPolicy.Decide(new FailoverContext
            {
                Generation = _state.Generation,
                CommandGeneration = _state.Generation,
                Mode = _catalogue.Settings.SelectionMode,
                CountryMode = _catalogue.Settings.CountryMode,
                StrictCountry = _catalogue.Settings.Country,
                ActiveNodeId = _state.ActiveNodeId,
                Failure = failure,
                ConsecutiveHealthFailures = payload.ConsecutiveFailures,
                SwitchesInLastMinute = _switchesInWindow,
                CooldownActive = false,
                Standbys = _standbys,
            });
            if (decision.Action is FailoverAction.Stay or FailoverAction.IgnoreStale)
            {
                stayed = true;
            }
            else if (decision.Action == FailoverAction.BlockOffline)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.UplinkLost, _state.Generation, _state.ActiveNodeId));
                _sequence++;
            }
            else if (decision.Action == FailoverAction.Switch && decision.NodeId is not null && _state.Phase == TunnelPhase.Connected)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.HealthFailed, _state.Generation, _state.ActiveNodeId, decision.ReasonCode));
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.SwitchCommitted, _state.Generation, decision.NodeId));
                _switchesInWindow++;
                _sequence++;
            }
            else
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, decision.ReasonCode));
                _sequence++;
            }
        }

        return stayed
            ? Ok(request, Snapshot(), "Сессия не переключена.")
            : Ok(request, Snapshot(), "Состояние сессии обновлено без объявления нового успешного канала.");
    }

    private IpcResponse ApplyRuntimeSet(IpcRequest request)
    {
        if (!request.Payload.TryGetProperty("standbys", out var standbysElement))
        {
            return Fail(request, "MALFORMED", "Нет списка запасных серверов.");
        }

        var standbys = standbysElement.Deserialize<List<StandbyCandidate>>(IpcJson.RequestOptions);
        if (standbys is null)
        {
            return Fail(request, "MALFORMED", "Список запасных серверов повреждён.");
        }

        lock (_gate)
        {
            _standbys = standbys;
            _sequence++;
        }

        var message = Snapshot().Phase == nameof(TunnelPhase.Connected)
            ? "Список запасных обновлён. Текущая сессия не перезапущена."
            : "Список запасных сохранён.";
        return Ok(request, Snapshot(), message);
    }

    private IpcResponse Recover(IpcRequest request)
    {
        var recovery = _journal?.Recover(_guard) ?? new JournalRecovery(true, null, null, 0, 0);
        if (!recovery.Completed)
        {
            return Fail(request, recovery.ReasonCode ?? ReasonCodes.WindowsNotValidated,
                recovery.QuarantinePath is null
                    ? "Свои сетевые правила не сняты: модуль защиты не подтверждён."
                    : "Журнал эффектов повреждён и отложен. Правила из него не считаются снятыми: " + recovery.QuarantinePath);
        }

        return Ok(request, Snapshot(), recovery.OpenEffects == 0
            ? "Своих сетевых правил нет. Восстанавливать нечего."
            : "Свои правила сняты.");
    }

    private CatalogueNode? Select(ConnectPayload payload, DateTimeOffset now)
    {
        var settings = _catalogue.Settings;
        var context = new EligibilityContext
        {
            NowUtc = now,
            NetworkEpoch = _catalogue.NetworkEpoch,
            AllowInsecureCertificates = settings.AllowInsecureCertificates,
            Purpose = string.IsNullOrWhiteSpace(payload.NodeId) ? SelectionPurpose.Automatic : SelectionPurpose.Manual,
            AllowedAge = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes),
            MaxAcceptableLatencyMs = settings.MaxAcceptableLatencyMs,
        };
        if (!string.IsNullOrWhiteSpace(payload.NodeId))
        {
            var node = _catalogue.Nodes.FirstOrDefault(item => item.NodeId == payload.NodeId && item.Digest == payload.Digest);
            if (node is null || !CountryAllows(node) || !MemoryCatalogue.IsEligible(node, context with
                {
                    DisabledFamilies = settings.DisabledFamilyIds.ToHashSet(StringComparer.Ordinal),
                }))
            {
                return null;
            }

            return node;
        }

        return _catalogue.Eligible(context)
            .Where(CountryAllows)
            .OrderBy(node => node.Assessment?.MedianLatencyMs ?? int.MaxValue)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private bool CountryAllows(CatalogueNode node)
    {
        if (_catalogue.Settings.CountryMode != CountryConstraint.Strict)
        {
            return true;
        }

        return string.Equals(node.AdvertisedCountry, _catalogue.Settings.Country, StringComparison.OrdinalIgnoreCase);
    }

    private static string NewSecret()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return Convert.ToHexString(bytes);
    }

    private static string BlockMessage(string? reason)
    {
        return reason switch
        {
            ReasonCodes.NotWindows => "На этой системе туннель Windows не запускается. Сеть компьютера не изменялась.",
            ReasonCodes.WindowsNotValidated => "Защита Windows ещё не проверена. Подключение не включается и не объявляется установленным.",
            _ => "Подключение остановлено до изменения сети.",
        };
    }

    private IpcResponse Ok(IpcRequest request, BrokerSnapshot snapshot, string? message = null)
    {
        return new IpcResponse
        {
            RequestId = request.RequestId,
            Ok = true,
            Message = message,
            StateRevision = snapshot.Revision,
            StateSequence = snapshot.Sequence,
            Snapshot = snapshot,
        };
    }

    private IpcResponse Fail(IpcRequest request, string? code, string message)
    {
        var snapshot = Snapshot();
        return new IpcResponse
        {
            RequestId = request.RequestId,
            Ok = false,
            ErrorCode = code,
            Message = message,
            StateRevision = snapshot.Revision,
            StateSequence = snapshot.Sequence,
            Snapshot = snapshot,
        };
    }
}
