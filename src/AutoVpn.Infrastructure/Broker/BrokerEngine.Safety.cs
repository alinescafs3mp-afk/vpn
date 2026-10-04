using System.Security.Cryptography;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Domain;
using Microsoft.Data.Sqlite;

namespace AutoVpn.Infrastructure.Broker;

public sealed partial class BrokerEngine
{
    private bool _catalogueAvailable = true;

    private bool RefreshCatalogueUnlocked()
    {
        try
        {
            (_catalogue as IRefreshableCatalogue)?.Refresh();
            _catalogueAvailable = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            CryptographicException or SqliteException or JsonException or ArgumentException)
        {
            _catalogueAvailable = false;
            if (_ownedResources.Count > 0 && !_state.DisconnectCommitted && _state.BlockReason != "CATALOGUE_UNAVAILABLE")
            {
                _operationId = null;
                _coreRunning = false;
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block,
                    _state.Generation, _state.ActiveNodeId, "CATALOGUE_UNAVAILABLE"));
                _sequence++;
            }
        }
        return _catalogueAvailable;
    }

    private bool ReplacementHeldUnlocked(CatalogueNode selected, long generation, string operation,
        long epoch, PolicyStamp policy)
    {
        var live = _catalogue.Nodes.FirstOrDefault(node => node.NodeId == selected.NodeId);
        return _catalogueAvailable && !_shutdownRequested && !_state.DisconnectCommitted &&
            _state.Generation == generation && _operationId == operation && _operationGeneration == generation &&
            _state.Phase == TunnelPhase.Reconnecting && _catalogue.NetworkEpoch == epoch &&
            policy.Equals(PolicyStamp.Capture(_catalogue.Settings)) && live is not null &&
            live.Digest == selected.Digest && !live.Excluded && IsCurrentlyEligible(live, NowUtc());
    }

    /// <summary>
    /// Local authority/liveness pulse, not a network health probe. On loss of
    /// authority, stop owned work but KEEP existing protection until explicit
    /// Disconnect. It must not become a direct-network fallback after an error.
    /// </summary>
    public async Task<bool> EnforceSafetyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CoreOwnership[] toStop;
        lock (_gate)
        {
            RefreshCatalogueUnlocked();
            ObserveCoreExitUnlocked();
            if (_ownedResources.Count == 0 || _state.DisconnectCommitted) return true;
            var reason = !_catalogueAvailable ? "CATALOGUE_UNAVAILABLE"
                : !_catalogue.Settings.DisclosureAccepted ? "DISCLOSURE_REVOKED"
                : _operationEpoch != _catalogue.NetworkEpoch ? "NETWORK_EPOCH_CHANGED"
                : _commitPolicy is PolicyStamp policy && !policy.Equals(PolicyStamp.Capture(_catalogue.Settings)) ? ReasonCodes.PolicyChanged
                : null;
            if (reason is null) return true;
            _operationId = null;
            _coreRunning = false;
            if (_state.BlockReason != reason)
            {
                _state = TunnelReducer.Apply(_state, new TunnelCommand(TunnelCommandKind.Block,
                    _state.Generation, _state.ActiveNodeId, reason));
                _sequence++;
            }
            toStop = _ownedResources.ToArray();
        }
        var complete = true;
        foreach (var resource in toStop)
        {
            // After invalidation cleanup is owned, even if the monitor's waiter is canceled.
            try { await StopOwnedAsync(resource.Generation, resource.OperationId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { complete = false; }
        }
        return complete;
    }
}
