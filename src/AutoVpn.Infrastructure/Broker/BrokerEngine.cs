using System.Globalization;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.Infrastructure.Broker;

public sealed partial class BrokerEngine
{
    private readonly ICatalogue _catalogue;
    private readonly INetworkGuard _guard;
    private readonly ICoreController _core;
    private readonly EffectJournal? _journal;
    private readonly IClock? _clock;
    private readonly IProbeTransport? _admission;
    private readonly Uri? _admissionTarget;
    private readonly string? _requiredTargetSetId;
    private bool _shutdownRequested;
    private readonly object _gate = new();
    private TunnelState _state = TunnelState.Initial;
    private long _sequence;
    private bool _coreRunning;
    private List<StandbyCandidate> _standbys = [];
    private readonly Queue<DateTimeOffset> _switchTimes = new();
    private DateTimeOffset? _cooldownUntil;
    private bool _switchBusy;
    private string? _operationId;
    private long _operationGeneration;
    private long _operationEpoch;
    // Resource ownership is separate from usability. Partial starts and failed stops
    // remain represented until the exact owned stop completes successfully.
    private readonly HashSet<CoreOwnership> _ownedResources = new();
    private string? _attemptDigest;
    private string? _ownedOperationId;
    private long _ownedGeneration;
    private bool? _sessionLanAccess;
    private SelectionPurpose _selectionPurpose = SelectionPurpose.Automatic;
    private PolicyStamp? _commitPolicy;
    private bool _cleanupPending;
    private bool _cleanupBusy;
    private string _cleanupOperationId = "";
    private long _cleanupGeneration;
    private int _lifecycle;

    public string BootId { get; } = Guid.NewGuid().ToString("N");

    public BrokerEngine(
        ICatalogue catalogue,
        INetworkGuard guard,
        ICoreController core,
        EffectJournal? journal = null,
        IClock? clock = null,
        IProbeTransport? admission = null,
        Uri? admissionTarget = null,
        string? requiredTargetSetId = null)
    {
        _catalogue = catalogue;
        _guard = guard;
        _core = core;
        _journal = journal;
        _clock = clock;
        _admission = admission;
        _admissionTarget = admissionTarget;
        if (requiredTargetSetId is not null && (requiredTargetSetId.Length != 64 || !requiredTargetSetId.All(Uri.IsHexDigit)))
            throw new ArgumentException("A complete two-target identity is required.", nameof(requiredTargetSetId));
        if (requiredTargetSetId is not null && admission is not null &&
            (admission is not TwoTargetProbeTransport pair || pair.TargetSetId != requiredTargetSetId || pair.PrimaryTarget != admissionTarget))
            throw new ArgumentException("Broker admission must use the same reviewed two-target contract.", nameof(admission));
        _requiredTargetSetId = requiredTargetSetId;
    }

    private DateTimeOffset NowUtc()
    {
        return _clock?.UtcNow ?? DateTimeOffset.UtcNow;
    }

    public TunnelState State
    {
        get
        {
            lock (_gate)
            {
                RefreshCatalogueUnlocked();
                ObserveCoreExitUnlocked();
                return _state;
            }
        }
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (IpcOperations.ChangesState(request.Operation) &&
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
        RefreshCatalogueUnlocked();
        ObserveCoreExitUnlocked();
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
            OwnedResourceCount = _ownedResources.Count,
            CoreVersion = ProductLimits.CoreVersion,
            CountryLabel = active?.AdvertisedCountry,
            ServerLabel = active?.Label,
            LatencyMs = active?.Assessment?.MedianLatencyMs,
            LastCheckUtc = active?.Assessment?.LastSuccessUtc,
            StandbyCount = _standbys.Count,
            OperationId = _operationId,
            BootId = BootId,
        };
    }

    private void ObserveCoreExitUnlocked()
    {
        if (!_coreRunning || _ownedOperationId is null || _core is not ICoreLiveness liveness ||
            liveness.IsRunning(_ownedGeneration, _ownedOperationId)) return;
        _coreRunning = false;
        _operationId = null;
        _state = TunnelReducer.Apply(_state, new TunnelCommand(
            _state.Phase == TunnelPhase.Connected ? TunnelCommandKind.CoreExited : TunnelCommandKind.Block,
            _state.Generation, _state.ActiveNodeId, "CORE_EXITED"));
        // Keep ownership/protection until the exact cleanup is confirmed.
        _sequence++;
    }

    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _shutdownRequested = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Snapshot();
            var response = await HandleAsync(new IpcRequest
            {
                ProtocolVersion = ProductLimits.IpcProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"), Operation = IpcOperations.Disconnect,
                ExpectedStateRevision = snapshot.Revision,
                Payload = JsonSerializer.SerializeToElement(new DisconnectPayload(), IpcJson.Options),
            }, CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.Ok)
            {
                var final = Snapshot();
                return !final.CoreRunning && final.OwnedResourceCount == 0 &&
                    !final.ProtectionArmed && final.Phase == nameof(TunnelPhase.Disconnected);
            }
            if (response.ErrorCode is not (ReasonCodes.StaleRevision or "BUSY")) return false;
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IpcResponse> ConnectAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_shutdownRequested) return Fail(request, "BROKER_CLOSING", "Брокер останавливается; новое подключение не начинается.");
            if (!RefreshCatalogueUnlocked()) return Fail(request, "CATALOGUE_UNAVAILABLE", "Актуальный каталог недоступен. Подключение не начинается.");
        }
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

        var now = NowUtc();
        var selected = await SelectReadyAsync(payload, now, cancellationToken).ConfigureAwait(false);
        if (selected is null)
        {
            return Fail(request, ReasonCodes.NoEligibleServer, Ru.NoServer);
        }

        var policy = PolicyStamp.Capture(_catalogue.Settings);
        string yaml;
        var sessionLan = LanAccess(request, payload);
        try
        {
            yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
            {
                Secret = NewSecret(),
                ControllerPort = 12789,
                SocksPort = null,
                Tun = true,
                LanAccess = sessionLan,
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
        var recoveryBusy = false;
        var staleRevision = false;
        long armedGeneration = 0;
        var operationId = "";
        lock (_gate)
        {
            if (request.ExpectedStateRevision != _state.Revision)
            {
                staleRevision = true;
            }
            else if (_lifecycle != 0 || _shutdownRequested)
            {
                recoveryBusy = true;
            }
            else if (_ownedResources.Count != 0 || _state.ProtectionArmed || _state.Phase is TunnelPhase.Connected or TunnelPhase.Connecting or TunnelPhase.PreparingProtection or TunnelPhase.Reconnecting or TunnelPhase.RestoringNetwork)
            {
                alreadyRunning = true;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                _attemptDigest = selected.Digest;
                _selectionPurpose = string.IsNullOrWhiteSpace(payload.NodeId) ? SelectionPurpose.Automatic : SelectionPurpose.Manual;
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Connect, _state.Generation, selected.NodeId));
                _sequence++;
                armedGeneration = _state.Generation;
                var protectionRequired = ProtectionRequired(request, payload);
                var explicitUnprotected = !protectionRequired && !_catalogue.Settings.ProtectionOnConnect;
                var armed = _guard.Arm(new GuardRequest(armedGeneration, LanAccess(request, payload), protectionRequired));
                if (explicitUnprotected)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.UnprotectedAccepted, _state.Generation, selected.NodeId));
                    _sequence++;
                    operationId = Guid.NewGuid().ToString("N");
                    _operationId = operationId;
                    _operationGeneration = armedGeneration;
                    _operationEpoch = _catalogue.NetworkEpoch;
                    _ownedOperationId = operationId;
                    _ownedGeneration = armedGeneration;
                    _sessionLanAccess = sessionLan;
                }
                else if (!armed.Armed)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.ProtectionFailed, _state.Generation, selected.NodeId, armed.ReasonCode));
                    _sequence++;
                    refusal = armed.ReasonCode ?? ReasonCodes.WindowsNotValidated;
                }
                else
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.ProtectionArmed, _state.Generation, selected.NodeId));
                    _sequence++;
                    operationId = Guid.NewGuid().ToString("N");
                    _operationId = operationId;
                    _operationGeneration = armedGeneration;
                    _operationEpoch = _catalogue.NetworkEpoch;
                    _ownedOperationId = operationId;
                    _ownedGeneration = armedGeneration;
                    _sessionLanAccess = sessionLan;
                }
            }
        }

        if (staleRevision)
        {
            return Fail(request, ReasonCodes.StaleRevision, "Состояние службы уже изменилось.");
        }

        if (recoveryBusy)
        {
            return Fail(request, "RECOVERY_BLOCKED", "Подключение не начинается: брокер останавливается или сверяет свои правила.");
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

        lock (_gate) RefreshCatalogueUnlocked();
        if (!_catalogueAvailable || !policy.Equals(PolicyStamp.Capture(_catalogue.Settings)))
        {
            lock (_gate)
            {
                if (_operationId == operationId && _state.Generation == armedGeneration)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed,
                        armedGeneration, selected.NodeId, ReasonCodes.PolicyChanged));
                    _sequence++;
                    _operationId = null;
                }
            }
            return Fail(request, ReasonCodes.PolicyChanged, "Настройки изменились при подготовке защиты. Профиль не запущен.");
        }

        CoreStartResult started;
        try
        {
            started = await StartOwnedAsync(yaml, armedGeneration, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            lock (_gate)
            {
                if (_state.Generation == armedGeneration && _state.Phase == TunnelPhase.Connecting)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, selected.NodeId, ReasonCodes.CoreConfigRejected));
                    _sequence++;
                }

                if (_operationId == operationId)
                {
                    _coreRunning = false;
                    _operationId = null;
                }
            }

            return Fail(request, ReasonCodes.CoreConfigRejected, "Ядро не запущено. Состояние защиты показано отдельно; очистка подтверждается отключением.");
        }
        catch (OperationCanceledException)
        {
            await StopOwnedAsync(armedGeneration, operationId, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (_state.Generation == armedGeneration && _state.Phase == TunnelPhase.Connecting)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, selected.NodeId, ReasonCodes.Canceled));
                    _sequence++;
                }

                if (_operationId == operationId)
                {
                    _coreRunning = false;
                    _operationId = null;
                }
            }

            return Fail(request, ReasonCodes.Canceled, "Подключение отменено. Ядро остановлено.");
        }

        lock (_gate) RefreshCatalogueUnlocked();
        var policyChanged = !_catalogueAvailable || !policy.Equals(PolicyStamp.Capture(_catalogue.Settings));
        var callerCanceled = cancellationToken.IsCancellationRequested;
        var abandon = false;
        string? startRefusal = null;
        lock (_gate)
        {
            var owned = _operationId == operationId &&
                        _operationGeneration == armedGeneration &&
                        _state.Generation == armedGeneration &&
                        !_state.DisconnectCommitted;
            if (!owned || callerCanceled)
            {
                abandon = true;
                if (owned)
                {
                    if (_state.Generation == armedGeneration && _state.Phase == TunnelPhase.Connecting)
                    {
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, selected.NodeId, ReasonCodes.Canceled));
                        _sequence++;
                    }

                    _coreRunning = false;
                    _operationId = null;
                }
            }
            else if (policyChanged || !SelectionHeld(selected))
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, selected.NodeId, ReasonCodes.PolicyChanged));
                _sequence++;
                _coreRunning = false;
                _operationId = null;
                startRefusal = ReasonCodes.PolicyChanged;
            }
            else if (!started.Started)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, selected.NodeId, started.ReasonCode));
                _sequence++;
                _coreRunning = false;
                _operationId = null;
                startRefusal = started.ReasonCode ?? ReasonCodes.CoreConfigRejected;
            }
            else
            {
                _coreRunning = true;
                _ownedOperationId = operationId;
                _ownedGeneration = armedGeneration;
                _commitPolicy = PolicyStamp.Capture(_catalogue.Settings);
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.CoreStarted, _state.Generation, selected.NodeId));
                _sequence++;
            }
        }

        if (abandon || startRefusal is not null)
        {
            await StopOwnedAsync(armedGeneration, operationId, CancellationToken.None).ConfigureAwait(false);
        }

        if (startRefusal is not null)
        {
            if (Owns(operationId, armedGeneration))
            {
                _catalogue.SetActiveNode(null);
            }

            var message = startRefusal == ReasonCodes.PolicyChanged
                ? "Настройки изменились во время запуска. Сессия не подтверждена."
                : "Ядро не запущено. Состояние защиты показано отдельно; очистка подтверждается отключением.";
            return Fail(request, startRefusal, message);
        }

        if (abandon || !Owns(operationId, armedGeneration))
        {
            return Fail(request, ReasonCodes.Canceled, "Подключение отменено. Ядро остановлено.");
        }

        _catalogue.SetActiveNode(selected.NodeId);
        return Ok(request, Snapshot(), "Ядро запущено. Канал ещё не подтверждён, состояние не «подключён».");
    }

    private readonly record struct CoreOwnership(long Generation, string OperationId);

    private async Task<CoreStartResult> StartOwnedAsync(string yaml, long generation,
        string operationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Serialize admission with Disconnect and safety-monitor invalidation.
            // A late task cannot publish a resource after cleanup was confirmed.
            if (_shutdownRequested || _state.DisconnectCommitted || _state.Generation != generation ||
                _operationId != operationId || _operationGeneration != generation)
                return new CoreStartResult(false, ReasonCodes.Canceled);
            _ownedResources.Add(new CoreOwnership(generation, operationId));
        }
        return await _core.StartAsync(yaml, generation, operationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopOwnedAsync(long generation, string operationId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await _core.StopAsync(generation, operationId, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        lock (_gate) { _ownedResources.Remove(new CoreOwnership(generation, operationId)); }
    }

    private bool Owns(string operationId, long generation)
    {
        lock (_gate)
        {
            return _operationId == operationId &&
                   _operationGeneration == generation &&
                   _state.Generation == generation &&
                   !_state.DisconnectCommitted &&
                   _coreRunning;
        }
    }

    /// <summary>
    /// In-process only. IPC cannot mark the tunnel connected.
    /// </summary>
    public void ConfirmProduction(string bootId, long generation, string? operationId, string? nodeId, long networkEpoch, bool ok, string? reasonCode)
    {
        lock (_gate)
        {
            RefreshCatalogueUnlocked();
            ObserveCoreExitUnlocked();
            if (!_catalogueAvailable || !string.Equals(bootId, BootId, StringComparison.Ordinal) ||
                generation != _state.Generation ||
                generation != _operationGeneration ||
                string.IsNullOrEmpty(operationId) ||
                !string.Equals(operationId, _operationId, StringComparison.Ordinal) ||
                !string.Equals(nodeId, _state.ActiveNodeId, StringComparison.Ordinal) ||
                networkEpoch != _operationEpoch ||
                networkEpoch != _catalogue.NetworkEpoch ||
                !_coreRunning ||
                _state.Phase is not (TunnelPhase.Connecting or TunnelPhase.Reconnecting))
            {
                return;
            }

            var current = _catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
            var settings = _catalogue.Settings;
            var policyMoved = _commitPolicy is PolicyStamp committed && !committed.Equals(PolicyStamp.Capture(settings));
            var blocked = current is null
                || (current.Excluded && _selectionPurpose != SelectionPurpose.Manual)
                || (current.Semantics.SkipCertVerify && !settings.AllowInsecureCertificates)
                || current.Digest != _attemptDigest
                || !IsCurrentlyEligible(current, NowUtc(), _selectionPurpose);
            if (policyMoved || blocked)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.VerifyFailed, _state.Generation, _state.ActiveNodeId, ReasonCodes.PolicyChanged));
                _sequence++;
                _operationId = null;
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
        _ = cancellationToken;
        long cleanupGeneration = 0;
        var cleanupOperation = "";
        var staleRevision = false;
        var busy = false;
        lock (_gate)
        {
            if (request.ExpectedStateRevision != _state.Revision)
            {
                staleRevision = true;
            }
            else if (_cleanupBusy)
            {
                busy = true;
            }
            else
            {
                _cleanupBusy = true;
                if (!_cleanupPending)
                {
                    _cleanupOperationId = _ownedOperationId ?? _operationId ?? "";
                    _cleanupGeneration = _ownedGeneration != 0 ? _ownedGeneration : _state.Generation;
                    _cleanupPending = true;
                    _operationId = null;
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Disconnect, _state.Generation));
                    _sequence++;
                }

                cleanupGeneration = _cleanupGeneration;
                cleanupOperation = _cleanupOperationId;
            }
        }

        if (staleRevision)
        {
            return Fail(request, ReasonCodes.StaleRevision, "Состояние службы уже изменилось.");
        }

        if (busy)
        {
            return Fail(request, "BUSY", "Отключение уже выполняется.");
        }

        try
        {
            try
            {
                CoreOwnership[] resources;
                lock (_gate) { resources = _ownedResources.ToArray(); }
                if (resources.Length == 0)
                    await StopOwnedAsync(cleanupGeneration, cleanupOperation, CancellationToken.None).ConfigureAwait(false);
                else
                    foreach (var resource in resources)
                        await StopOwnedAsync(resource.Generation, resource.OperationId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Fail(request, "CLEANUP_UNCERTAIN", "Остановка ядра не подтверждена. Повтор отключения снимает те же ресурсы.");
            }

            lock (_gate)
            {
                if (_cleanupGeneration == cleanupGeneration)
                {
                    _coreRunning = false;
                    if (string.Equals(_ownedOperationId, cleanupOperation, StringComparison.Ordinal))
                    {
                        _ownedOperationId = null;
                        _ownedGeneration = 0;
                    }
                }
            }

            var recovery = _journal?.Recover(_guard);
            if (recovery is { Completed: false })
            {
                return Fail(request, recovery.ReasonCode ?? ReasonCodes.WindowsNotValidated,
                    recovery.QuarantinePath is null
                        ? "Отключение запрошено, но свои сетевые правила не подтверждены как снятые."
                        : "Журнал эффектов повреждён и отложен. Правила из него не считаются снятыми: " + recovery.QuarantinePath);
            }

            var disarm = _guard.Disarm(cleanupGeneration);
            if (!disarm.Completed)
            {
                return Fail(request, disarm.ReasonCode ?? "CLEANUP_UNCERTAIN", "Снятие защиты не подтверждено. Повтор отключения снимает те же ресурсы.");
            }

            _catalogue.SetActiveNode(null);
            lock (_gate)
            {
                if (_cleanupPending && _cleanupGeneration == cleanupGeneration && _state.Phase == TunnelPhase.RestoringNetwork)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.RestoreFinished, _state.Generation));
                    _sequence++;
                    _cleanupPending = false;
                }
            }

            return Ok(request, Snapshot(), "Подключение остановлено.");
        }
        finally
        {
            lock (_gate)
            {
                _cleanupBusy = false;
            }
        }
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

        var now = NowUtc();
        var stayed = false;
        CatalogueNode? switchTarget = null;
        long switchGeneration = 0;
        string? switchOperation = null;
        CoreOwnership[] replacing = [];
        var staleRevision = false;
        lock (_gate)
        {
            if (request.ExpectedStateRevision != _state.Revision)
            {
                staleRevision = true;
            }
            else
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
                if (decision.Action == FailoverAction.HoldProtected)
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.CoreExited, _state.Generation, _state.ActiveNodeId, decision.ReasonCode));
                    _sequence++;
                    _coreRunning = false;
                    _operationId = null;
                    _cooldownUntil ??= now.AddSeconds(ProductLimits.SwitchCooldownSeconds);
                }
                else if (decision.Action is FailoverAction.Stay or FailoverAction.IgnoreStale or FailoverAction.WaitCooldown or FailoverAction.DiagnoseTargets)
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
                else if (_switchBusy)
                {
                    stayed = true;
                }
                else if (decision.Action == FailoverAction.Switch && decision.NodeId is not null && _state.Phase is TunnelPhase.Connected or TunnelPhase.Reconnecting)
                {
                    var candidate = _catalogue.Nodes.FirstOrDefault(node => node.NodeId == decision.NodeId);
                    if (candidate is null || !IsCurrentlyEligible(candidate, now))
                    {
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, ReasonCodes.NoEligibleServer));
                        _sequence++;
                        if (failure == FailureKind.CoreExit)
                        {
                            _coreRunning = false;
                            _operationId = null;
                        }
                    }
                    else
                    {
                        if (failure == FailureKind.CoreExit && _state.Phase == TunnelPhase.Connected)
                        {
                            _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.CoreExited, _state.Generation, _state.ActiveNodeId, decision.ReasonCode));
                            _sequence++;
                            _coreRunning = false;
                        }

                        _switchBusy = true;
                        replacing = _ownedResources.ToArray();
                        // No green interval while old cleanup / new readiness is pending.
                        if (_state.Phase == TunnelPhase.Connected)
                            _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.HealthFailed,
                                _state.Generation, _state.ActiveNodeId));
                        _coreRunning = false;
                        _sequence++;
                        switchTarget = candidate;
                        switchGeneration = _state.Generation;
                        switchOperation = Guid.NewGuid().ToString("N");
                        _selectionPurpose = SelectionPurpose.Automatic;
                        _attemptDigest = candidate.Digest;
                        _operationId = switchOperation;
                        _operationGeneration = switchGeneration;
                        _operationEpoch = _catalogue.NetworkEpoch;
                    }
                }
                else
                {
                    _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block, _state.Generation, _state.ActiveNodeId, decision.ReasonCode));
                    _sequence++;
                    if (failure == FailureKind.CoreExit)
                    {
                        _coreRunning = false;
                        _operationId = null;
                    }
                }
            }
        }

        if (staleRevision)
        {
            return Fail(request, ReasonCodes.StaleRevision, "Состояние службы уже изменилось.");
        }

        if (switchTarget is not null)
        {
            var switched = false;
            var reservedNew = false;
            string? replacementFailure = null;
            try
            {
                var policy = PolicyStamp.Capture(_catalogue.Settings);
                var epoch = _operationEpoch;
                var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
                {
                    Secret = NewSecret(), ControllerPort = 12789, Tun = true,
                    LanAccess = _sessionLanAccess ?? _catalogue.Settings.LanAccess,
                    AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
                    Nodes = [NodeWireFactory.FromCatalogue(switchTarget)], SelectedNodeId = switchTarget.NodeId,
                });
                // Never overlap runtimes. Failed/uncertain old cleanup aborts the
                // replacement and retains that exact old ownership for Disconnect.
                foreach (var previous in replacing)
                    await StopOwnedAsync(previous.Generation, previous.OperationId, CancellationToken.None).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    RefreshCatalogueUnlocked();
                    if (!ReplacementHeldUnlocked(switchTarget, switchGeneration, switchOperation!, epoch, policy))
                        throw new OperationCanceledException("REPLACEMENT_SUPERSEDED");
                    // Reserve before yielding so Disconnect also fences a not-yet-started replacement.
                    _ownedResources.Add(new CoreOwnership(switchGeneration, switchOperation!));
                    _ownedOperationId = switchOperation;
                    _ownedGeneration = switchGeneration;
                    reservedNew = true;
                }
                var started = await StartOwnedAsync(yaml, switchGeneration, switchOperation!, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    RefreshCatalogueUnlocked();
                    if (!cancellationToken.IsCancellationRequested && started.Started &&
                        ReplacementHeldUnlocked(switchTarget, switchGeneration, switchOperation!, epoch, policy))
                    {
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.SwitchCommitted,
                            _state.Generation, switchTarget.NodeId));
                        _switchTimes.Enqueue(NowUtc());
                        if (_switchTimes.Count >= ProductLimits.MaxSwitchesPerMinute)
                            _cooldownUntil = NowUtc().AddSeconds(ProductLimits.SwitchCooldownSeconds);
                        _coreRunning = true;
                        _commitPolicy = policy;
                        _sequence++;
                        switched = true;
                    }
                    else replacementFailure = started.ReasonCode ?? ReasonCodes.PolicyChanged;
                }
            }
            catch (OperationCanceledException) { replacementFailure = ReasonCodes.Canceled; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            { replacementFailure = "REPLACEMENT_CLEANUP_OR_START_FAILED"; }
            finally
            {
                if (!switched && reservedNew)
                {
                    try { await StopOwnedAsync(switchGeneration, switchOperation!, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { replacementFailure = "CLEANUP_UNCERTAIN"; }
                }
                lock (_gate)
                {
                    if (!switched && _state.Generation == switchGeneration && !_state.DisconnectCommitted &&
                        _operationId == switchOperation)
                    {
                        _operationId = null;
                        _coreRunning = false;
                        _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block,
                            _state.Generation, _state.ActiveNodeId, replacementFailure ?? ReasonCodes.CoreConfigRejected));
                        _sequence++;
                    }
                    _switchBusy = false;
                }
            }
            if (switched && Owns(switchOperation!, switchGeneration)) _catalogue.SetActiveNode(switchTarget.NodeId);
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

        var now = NowUtc();
        var accepted = new List<StandbyCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in standbys)
        {
            if (accepted.Count >= ProductLimits.WarmStandbys)
            {
                break;
            }

            if (string.IsNullOrEmpty(item.NodeId) || !seen.Add(item.NodeId))
            {
                continue;
            }

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

        var staleRevision = false;
        lock (_gate)
        {
            if (request.ExpectedStateRevision != _state.Revision)
            {
                staleRevision = true;
            }
            else
            {
                _standbys = accepted;
                _state = _state with { Revision = _state.Revision + 1 };
                _sequence++;
            }
        }

        if (staleRevision)
        {
            return Fail(request, ReasonCodes.StaleRevision, "Состояние службы уже изменилось.");
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
        var blocked = false;
        var staleRevision = false;
        lock (_gate)
        {
            if (request.ExpectedStateRevision != _state.Revision)
            {
                staleRevision = true;
            }
            else if (_ownedResources.Count != 0 || _lifecycle != 0 || _cleanupPending || _cleanupBusy || _state.ProtectionArmed || _state.Phase is TunnelPhase.PreparingProtection or TunnelPhase.Connecting or TunnelPhase.Connected or TunnelPhase.Reconnecting or TunnelPhase.RestoringNetwork)
            {
                blocked = true;
            }
            else
            {
                _lifecycle = 1;
            }
        }

        if (staleRevision)
        {
            return Fail(request, ReasonCodes.StaleRevision, "Состояние службы уже изменилось.");
        }

        if (blocked)
        {
            return Fail(request, "RECOVERY_BLOCKED", "Восстановление не снимает защиту активной сессии.");
        }

        JournalRecovery recovery;
        try
        {
            recovery = _journal?.Recover(_guard) ?? new JournalRecovery(true, null, null, 0, 0);
        }
        finally
        {
            lock (_gate)
            {
                _lifecycle = 0;
            }
        }

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

    private async Task<CatalogueNode?> SelectReadyAsync(ConnectPayload payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var selected = Select(payload, now);
        if (selected is not null || _admission is null || _admissionTarget is null)
        {
            return selected;
        }

        var candidate = AdmissionCandidate(payload, now);
        if (candidate is null)
        {
            return null;
        }

        var purpose = string.IsNullOrWhiteSpace(payload.NodeId) ? SelectionPurpose.Automatic : SelectionPurpose.Manual;
        var admitted = await ProbeCoordinator.AdmitIfStaleAsync(
            _catalogue,
            _admission,
            _admissionTarget,
            candidate.NodeId,
            now,
            cancellationToken,
            purpose).ConfigureAwait(false);
        return admitted ? Select(payload, NowUtc()) : null;
    }

    private CatalogueNode? AdmissionCandidate(ConnectPayload payload, DateTimeOffset now)
    {
        var settings = _catalogue.Settings;
        var context = new EligibilityContext
        {
            RequiredTargetSetId = _requiredTargetSetId,
            NowUtc = now,
            NetworkEpoch = _catalogue.NetworkEpoch,
            AllowInsecureCertificates = settings.AllowInsecureCertificates,
            Purpose = string.IsNullOrWhiteSpace(payload.NodeId) ? SelectionPurpose.Automatic : SelectionPurpose.Manual,
            AllowedAge = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes),
            MaxAcceptableLatencyMs = settings.MaxAcceptableLatencyMs,
            DisabledFamilies = settings.DisabledFamilyIds.ToHashSet(StringComparer.Ordinal),
        };
        if (!string.IsNullOrWhiteSpace(payload.NodeId))
        {
            var node = _catalogue.Nodes.FirstOrDefault(item => item.NodeId == payload.NodeId && item.Digest == payload.Digest);
            if (node is null || !CountryAllows(node) || !MemoryCatalogue.IsEligible(node, context))
            {
                return null;
            }

            return ProbeCoordinator.NeedsOnDemandAdmission(node, now, _catalogue.NetworkEpoch) ? node : null;
        }

        return _catalogue.Eligible(context)
            .Where(node => CountryAllows(node) && ProbeCoordinator.NeedsOnDemandAdmission(node, now, _catalogue.NetworkEpoch))
            .OrderBy(node => Ranker.Cost(Rank(node)))
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private CatalogueNode? Select(ConnectPayload payload, DateTimeOffset now)
    {
        var settings = _catalogue.Settings;
        var context = new EligibilityContext
        {
            RequiredTargetSetId = _requiredTargetSetId,
            NowUtc = now,
            NetworkEpoch = _catalogue.NetworkEpoch,
            AllowInsecureCertificates = settings.AllowInsecureCertificates,
            Purpose = string.IsNullOrWhiteSpace(payload.NodeId) ? SelectionPurpose.Automatic : SelectionPurpose.Manual,
            AllowedAge = TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds),
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
        var recovery = _journal?.Recover(_guard);
        if (recovery is { Completed: false })
        {
            return;
        }

        var disarm = _guard.Disarm(generation);
        if (!disarm.Completed)
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

    private bool SelectionHeld(CatalogueNode selected)
    {
        if (_catalogue.NetworkEpoch != _operationEpoch)
        {
            return false;
        }

        var live = _catalogue.Nodes.FirstOrDefault(item => item.NodeId == selected.NodeId);
        return live is not null
            && string.Equals(live.Digest, selected.Digest, StringComparison.Ordinal)
            && IsCurrentlyEligible(live, NowUtc(), _selectionPurpose);
    }

    private bool IsCurrentlyEligible(CatalogueNode node, DateTimeOffset now, SelectionPurpose purpose = SelectionPurpose.PreConnect)
    {
        var settings = _catalogue.Settings;
        return settings.DisclosureAccepted && CountryAllows(node) && MemoryCatalogue.IsEligible(node, new EligibilityContext
        {
            RequiredTargetSetId = _requiredTargetSetId,
            NowUtc = now,
            NetworkEpoch = _catalogue.NetworkEpoch,
            AllowInsecureCertificates = settings.AllowInsecureCertificates,
            Purpose = purpose,
            AllowedAge = TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds),
            MaxAcceptableLatencyMs = settings.MaxAcceptableLatencyMs,
            DisabledFamilies = settings.DisabledFamilyIds.ToHashSet(StringComparer.Ordinal),
        });
    }

    private bool ProtectionRequired(IpcRequest request, ConnectPayload payload)
    {
        if (request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("protectionRequired", out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return _catalogue.Settings.ProtectionOnConnect;
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

    private readonly record struct PolicyStamp(
        int Revision,
        bool DisclosureAccepted,
        bool AllowInsecureCertificates,
        bool LanAccess,
        bool ProtectionOnConnect,
        CountryConstraint CountryMode,
        string? Country,
        string DisabledFamilies)
    {
        public static PolicyStamp Capture(ProductSettings settings)
        {
            var disabled = string.Join('\n', settings.DisabledFamilyIds.OrderBy(id => id, StringComparer.Ordinal));
            return new PolicyStamp(
                settings.Revision,
                settings.DisclosureAccepted,
                settings.AllowInsecureCertificates,
                settings.LanAccess,
                settings.ProtectionOnConnect,
                settings.CountryMode,
                settings.Country,
                disabled);
        }
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
