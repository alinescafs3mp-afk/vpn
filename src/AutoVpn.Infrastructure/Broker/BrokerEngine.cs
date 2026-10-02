using System.Globalization;
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
    private readonly Queue<DateTimeOffset> _switchTimes = new();
    private DateTimeOffset? _cooldownUntil;
    private bool _switchBusy;

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
            if (IpcOperations.ChangesState(request.Operation) &&
                request.ExpectedStateRevision != 0 &&
                request.ExpectedStateRevision != Snapshot().Revision)
            {
                return Fail(request, ReasonCodes.StaleRevision, "Состояние службы уже изменилось.");
            }

            return request.Operation switch
            {
                IpcOperations.GetSnapshot => Ok(request, Snapshot()),
                IpcOperations.Connect => await ConnectAsync(request, cancellationToken).ConfigureAwait(false),
                IpcOperations.Disconnect => await DisconnectAsync(request, cancellationToken).ConfigureAwait(false),
                IpcOperations.ReportHealth => await ReportHealthAsync(request, cancellationToken).ConfigureAwait(false),
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

        string yaml;
        try
        {
            yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
            {
                Secret = NewSecret(),
                ControllerPort = 12789,
                SocksPort = null,
                Tun = true,
                LanAccess = LanAccess(request, payload),
                AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
                Nodes = [NodeWireFactory.FromCatalogue(selected)],
                SelectedNodeId = selected.NodeId,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Fail(request, string.IsNullOrWhiteSpace(ex.Message) ? ReasonCodes.CoreConfigRejected : ex.Message,
                "Профиль ядра отклонён до изменения сети.");
        }

        var alreadyRunning = false;
        string? refusal = null;
        var didArm = false;
        long armedGeneration = 0;
        lock (_gate)
        {
            if (_state.ProtectionArmed || _state.Phase is TunnelPhase.Connected or TunnelPhase.Connecting or TunnelPhase.PreparingProtection or TunnelPhase.Reconnecting or TunnelPhase.RestoringNetwork)
            {
                alreadyRunning = true;
            }
            else
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Connect, _state.Generation, selected.NodeId));
                _sequence++;
                armedGeneration = _state.Generation;
                var armed = _guard.Arm(new GuardRequest(armedGeneration, LanAccess(request, payload), true));
                if (!armed.Armed)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.ProtectionFailed, _state.Generation, selected.NodeId, armed.ReasonCode));
                    _sequence++;
                    refusal = armed.ReasonCode ?? ReasonCodes.WindowsNotValidated;
                }
                else
                {
                    didArm = true;
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

        var started = await _core.StartAsync(yaml, cancellationToken).ConfigureAwait(false);
        var abandon = false;
        string? startRefusal = null;
        lock (_gate)
        {
            abandon = _state.Generation != armedGeneration || _state.DisconnectCommitted;
            if (abandon)
            {
                _coreRunning = false;
            }
            else if (!started.Started)
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

        if (abandon || startRefusal is not null)
        {
            await _core.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (startRefusal is not null)
        {
            if (didArm)
            {
                ReleaseProtection(armedGeneration);
            }

            _catalogue.SetActiveNode(null);
            return Fail(request, startRefusal, "Ядро не запущено. Подключение не объявлено.");
        }

        if (abandon)
        {
            _catalogue.SetActiveNode(null);
            return Fail(request, ReasonCodes.Canceled, "Подключение отменено. Ядро остановлено.");
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
        long armedGeneration;
        lock (_gate)
        {
            armedGeneration = _state.Generation;
            _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Disconnect, armedGeneration));
            _sequence++;
        }

        await _core.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _coreRunning = false;
        }

        _catalogue.SetActiveNode(null);
        var disarm = _guard.Disarm(armedGeneration);
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

    private async Task<IpcResponse> ReportHealthAsync(IpcRequest request, CancellationToken cancellationToken)
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

        var now = DateTimeOffset.UtcNow;
        var stayed = false;
        CatalogueNode? switchTarget = null;
        long switchGeneration = 0;
        lock (_gate)
        {
            PruneSwitches(now);
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
                SwitchesInLastMinute = _switchTimes.Count,
                CooldownActive = _cooldownUntil is DateTimeOffset until && until > now,
                Standbys = _standbys,
            });
            if (decision.Action is FailoverAction.Stay or FailoverAction.IgnoreStale or FailoverAction.WaitCooldown or FailoverAction.DiagnoseTargets)
            {
                stayed = true;
                if (decision.Action == FailoverAction.WaitCooldown && _cooldownUntil is null)
                {
                    _cooldownUntil = now.AddSeconds(ProductLimits.SwitchCooldownSeconds);
                }
            }
            else if (decision.Action == FailoverAction.BlockOffline)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.UplinkLost, _state.Generation, _state.ActiveNodeId));
                _sequence++;
            }
            else if (decision.Action == FailoverAction.Switch && decision.NodeId is not null && _state.Phase == TunnelPhase.Connected && !_switchBusy)
            {
                var candidate = _catalogue.Nodes.FirstOrDefault(node => node.NodeId == decision.NodeId);
                if (candidate is null || !IsCurrentlyEligible(candidate, now))
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, ReasonCodes.NoEligibleServer));
                    _sequence++;
                }
                else
                {
                    _switchBusy = true;
                    switchTarget = candidate;
                    switchGeneration = _state.Generation;
                }
            }
            else
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, decision.ReasonCode));
                _sequence++;
            }
        }

        if (switchTarget is not null)
        {
            var switched = false;
            try
            {
                var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
                {
                    Secret = NewSecret(),
                    ControllerPort = 12789,
                    Tun = true,
                    LanAccess = _catalogue.Settings.LanAccess,
                    AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
                    Nodes = [NodeWireFactory.FromCatalogue(switchTarget)],
                    SelectedNodeId = switchTarget.NodeId,
                });
                var started = await _core.StartAsync(yaml, cancellationToken).ConfigureAwait(false);
                var abandon = false;
                lock (_gate)
                {
                    abandon = _state.Generation != switchGeneration || _state.DisconnectCommitted || _state.Phase != TunnelPhase.Connected;
                    if (!abandon && started.Started)
                    {
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.HealthFailed, _state.Generation, _state.ActiveNodeId));
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.SwitchCommitted, _state.Generation, switchTarget.NodeId));
                        _switchTimes.Enqueue(now);
                        if (_switchTimes.Count >= ProductLimits.MaxSwitchesPerMinute)
                        {
                            _cooldownUntil = now.AddSeconds(ProductLimits.SwitchCooldownSeconds);
                        }

                        _coreRunning = true;
                        _sequence++;
                        switched = true;
                    }
                    else if (!abandon)
                    {
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, started.ReasonCode ?? ReasonCodes.CoreConfigRejected));
                        _sequence++;
                        _coreRunning = false;
                    }
                }

                if (!switched)
                {
                    await _core.StopAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                lock (_gate)
                {
                    if (_state.Generation == switchGeneration && _state.Phase == TunnelPhase.Connected)
                    {
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, ReasonCodes.CoreConfigRejected));
                        _sequence++;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _switchBusy = false;
                }
            }

            if (switched)
            {
                _catalogue.SetActiveNode(switchTarget.NodeId);
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

        var now = DateTimeOffset.UtcNow;
        var accepted = new List<StandbyCandidate>();
        foreach (var item in standbys)
        {
            var node = _catalogue.Nodes.FirstOrDefault(candidate => candidate.NodeId == item.NodeId);
            if (node is null || !IsCurrentlyEligible(node, now) || !CountryAllows(node))
            {
                continue;
            }

            accepted.Add(new StandbyCandidate
            {
                NodeId = node.NodeId,
                EndpointKey = node.Semantics.Host + ":" + node.Semantics.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Country = node.AdvertisedCountry ?? "",
                SourceFamilyId = node.CurrentFamilies.FirstOrDefault() ?? node.HistoricalFamilies.FirstOrDefault() ?? "",
                Cost = Ranker.Cost(Rank(node)),
                FreshOnEpoch = true,
            });
        }

        lock (_gate)
        {
            _standbys = accepted;
            _sequence++;
        }

        var connected = Snapshot().Phase == nameof(TunnelPhase.Connected);
        var message = accepted.Count == 0 && standbys.Count > 0
            ? "Запасные серверы не приняты: нет локально проверенных кандидатов."
            : connected
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
            .OrderBy(node => Ranker.Cost(Rank(node)))
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private void ReleaseProtection(long generation)
    {
        var disarm = _guard.Disarm(generation);
        var recovery = _journal?.Recover(_guard);
        if (!disarm.Completed || recovery is { Completed: false })
        {
            return;
        }

        lock (_gate)
        {
            if (_state.Generation == generation && _state.Phase == TunnelPhase.Blocked)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.ProtectionReleased, generation));
                _sequence++;
            }
        }
    }

    private void PruneSwitches(DateTimeOffset now)
    {
        while (_switchTimes.Count > 0 && now - _switchTimes.Peek() >= TimeSpan.FromMinutes(1))
        {
            _switchTimes.Dequeue();
        }

        if (_cooldownUntil is DateTimeOffset until && until <= now)
        {
            _cooldownUntil = null;
        }
    }

    private bool IsCurrentlyEligible(CatalogueNode node, DateTimeOffset now)
    {
        var settings = _catalogue.Settings;
        return MemoryCatalogue.IsEligible(node, new EligibilityContext
        {
            NowUtc = now,
            NetworkEpoch = _catalogue.NetworkEpoch,
            AllowInsecureCertificates = settings.AllowInsecureCertificates,
            Purpose = SelectionPurpose.Automatic,
            AllowedAge = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes),
            MaxAcceptableLatencyMs = settings.MaxAcceptableLatencyMs,
            DisabledFamilies = settings.DisabledFamilyIds.ToHashSet(StringComparer.Ordinal),
        });
    }

    private bool LanAccess(IpcRequest request, ConnectPayload payload)
    {
        _ = payload;
        if (request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("lanAccess", out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return _catalogue.Settings.LanAccess;
    }

    private static RankSample Rank(CatalogueNode node)
    {
        return new RankSample
        {
            NodeId = node.NodeId,
            EndpointKey = node.Semantics.Host + ":" + node.Semantics.Port.ToString(CultureInfo.InvariantCulture),
            MedianLatencyMs = node.Assessment?.MedianLatencyMs,
            SampleCount = node.Assessment?.MedianLatencyMs is int ? 1 : 0,
            FailureCount = node.Assessment?.ConsecutiveFailures ?? 0,
            FlapsLastHour = 0,
        };
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
