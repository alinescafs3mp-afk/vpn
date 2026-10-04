using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoVpn.UnitTests;

public sealed class AstraV3LiveCatalogueTests
{
    [Fact]
    public void ReadOnlySnapshotRefreshesOnlyAfterAnExplicitPulse()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); var old = reader.Settings;
        writer.Settings = old with { DisclosureAccepted = false, Revision = old.Revision + 1 };
        Assert.True(reader.Settings.DisclosureAccepted);
        reader.Refresh(); Assert.False(reader.Settings.DisclosureAccepted);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(writer.Settings), System.Text.Json.JsonSerializer.Serialize(reader.Settings)); Assert.Equal(writer.NetworkEpoch, reader.NetworkEpoch);
    }

    [Fact]
    public void BrokerReadsCurrentAssessmentWithoutReopeningAndHasNoWriteConflict()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); var node = writer.Nodes[0];
        for (var i = 0; i < 30; i++)
        {
            var updated = node.Assessment! with { MedianLatencyMs = 20 + i, LastAttemptUtc = LiveHarness.Now.AddSeconds(i) };
            writer.ApplyAssessment(node.NodeId, updated); reader.Refresh();
            Assert.Equal(20 + i, reader.Nodes.Single(n => n.NodeId == node.NodeId).Assessment!.MedianLatencyMs);
            reader.SetActiveNode(i % 2 == 0 ? node.NodeId : null);
        }
        Assert.True(writer.TrySetFavorite(node.NodeId, true)); reader.Refresh();
        Assert.True(reader.Nodes.Single(n => n.NodeId == node.NodeId).Favorite);
    }

    [Fact]
    public void RepeatedHealthUpdatesReuseProtectedSemanticsCache()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        var counting = new CountingProtector(); using var reader = SqliteCatalogue.OpenReadOnly(d.Path, counting);
        Assert.Equal(2, counting.Reads);
        for (var i = 0; i < 20; i++)
        {
            var node = writer.Nodes[0]; writer.ApplyAssessment(node.NodeId, node.Assessment! with { MedianLatencyMs = 50 + i });
            reader.Refresh();
        }
        Assert.Equal(2, counting.Reads);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("epoch")]
    [InlineData("assessment")]
    [InlineData("favorite")]
    [InlineData("excluded")]
    [InlineData("evict")]
    [InlineData("snapshot")]
    public void ReadOnlyBrokerCannotBecomeASecondPersistentWriter(string operation)
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); var node = reader.Nodes[0];
        Action write = operation switch
        {
            "settings" => () => reader.Settings = reader.Settings with { DisclosureAccepted = false },
            "epoch" => () => reader.SetNetworkEpoch(reader.NetworkEpoch + 1),
            "assessment" => () => reader.ApplyAssessment(node.NodeId, node.Assessment! with { Health = HealthState.Pending }),
            "favorite" => () => reader.TrySetFavorite(node.NodeId, true),
            "excluded" => () => reader.TrySetExcluded(node.NodeId, true),
            "evict" => () => reader.EvictOverflow(LiveHarness.Now),
            _ => () => reader.ApplySnapshot(new SnapshotCommit { ArtifactId = "empty", FamilyId = "black-vless", ContentHash = "empty", Complete = true, NowUtc = LiveHarness.Now, Nodes = [] }),
        };
        Assert.Equal("CATALOGUE_READ_ONLY", Assert.Throws<CatalogueStoreException>(write).Message);
        using var secondReader = d.Read(); Assert.True(secondReader.Settings.DisclosureAccepted); Assert.Equal(2, secondReader.Nodes.Count);
        Assert.True(writer.TrySetFavorite(node.NodeId, true));
    }

    [Fact]
    public void RuntimeRetentionIsEphemeralAndSurvivesReaderRefresh()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        var first = writer.Nodes[0].NodeId; var second = writer.Nodes[1].NodeId;
        writer.SetActiveNode(first); using var reader = d.Read();
        Assert.DoesNotContain(reader.Nodes, n => n.ActiveSession);
        reader.SetActiveNode(second); writer.Settings = writer.Settings with { Revision = 2 };
        reader.Refresh(); Assert.Equal(second, Assert.Single(reader.Nodes, n => n.ActiveSession).NodeId);
        Assert.Equal(first, Assert.Single(writer.Nodes, n => n.ActiveSession).NodeId);
        using var another = d.Read(); Assert.DoesNotContain(another.Nodes, n => n.ActiveSession);
        reader.SetActiveNode(null); Assert.DoesNotContain(reader.Nodes, n => n.ActiveSession);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("future")]
    public void ReadOnlyOpenRefusesInvalidStoreWithoutCreatingOrQuarantining(string kind)
    {
        using var d = new Store(); byte[]? before = null;
        if (kind == "corrupt") File.WriteAllText(d.Path, "not a database");
        if (kind == "future")
        {
            using (var writer = d.Open()) LiveHarness.Populate(writer);
            d.Sql("UPDATE meta SET value='99' WHERE key='schema_version';");
        }
        if (File.Exists(d.Path)) before = File.ReadAllBytes(d.Path);
        Assert.ThrowsAny<Exception>(() => d.Read());
        Assert.Empty(Directory.GetFiles(d.Directory, "*.quarantine-*"));
        if (before is null) Assert.False(File.Exists(d.Path)); else Assert.Equal(before, File.ReadAllBytes(d.Path));
    }

    [Fact]
    public async Task BrokerRevocationUsesLatestReaderSnapshotAndLeavesOwnerCatalogueIntact()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); await using var h = new LiveHarness(reader); await h.StartConnected();
        writer.Settings = writer.Settings with { DisclosureAccepted = false, Revision = 2 };
        Assert.True(await h.Engine.EnforceSafetyAsync(default));
        Assert.False(h.Engine.Snapshot().CoreRunning); Assert.True(h.Engine.Snapshot().ProtectionArmed);
        Assert.Equal(1, h.Processes[0].Stops); Assert.Equal(2, writer.Nodes.Count);
        Assert.True((await h.Disconnect()).Ok); Assert.True(writer.TrySetFavorite(writer.Nodes[0].NodeId, true));
    }

    [Fact]
    public async Task ReadFailureDoesNotKeepGreenStateOrReanimateAfterStoreRecovers()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); await using var h = new LiveHarness(reader); await h.StartConnected(); var old = h.Engine.Snapshot();
        d.Sql("UPDATE meta SET value='99' WHERE key='schema_version';");
        Assert.Equal("CATALOGUE_UNAVAILABLE", h.Engine.Snapshot().BlockReason);
        Assert.True(await h.Engine.EnforceSafetyAsync(default)); Assert.Equal(1, h.Processes[0].Stops);
        d.Sql("UPDATE meta SET value='1' WHERE key='schema_version';"); reader.Refresh();
        h.Engine.ConfirmProduction(old.BootId!, old.Generation, old.OperationId, old.ActiveNodeId, reader.NetworkEpoch, true, null);
        Assert.NotEqual(TunnelPhase.Connected, h.Engine.State.Phase); Assert.False(h.Engine.Snapshot().CoreRunning);
        Assert.True(h.Engine.Snapshot().ProtectionArmed);
    }

    [Fact]
    public void FailedTransactionalLoadRetainsExactLastSnapshotButReportsFailure()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); var before = reader.Nodes; var settings = reader.Settings;
        d.Sql("UPDATE meta SET value='broken-json' WHERE key='settings'; UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='catalogue_revision';");
        Assert.ThrowsAny<Exception>(reader.Refresh);
        Assert.Equal(settings, reader.Settings); Assert.Equal(before.Select(n => n.Digest), reader.Nodes.Select(n => n.Digest));
    }

    [Fact]
    public void RevisionRegressionIsRefusedInsteadOfSilentlyRebasing()
    {
        using var d = new Store(); using var writer = d.Open(); LiveHarness.Populate(writer);
        using var reader = d.Read(); d.Sql("UPDATE meta SET value='0' WHERE key='catalogue_revision';");
        Assert.Equal("CATALOGUE_REVISION_REGRESSED", Assert.Throws<CatalogueStoreException>(reader.Refresh).Message);
    }

    [Fact]
    public void ExistingWritableConflictStillRequiresExplicitResolution()
    {
        using var d = new Store(); using var first = d.Open(); LiveHarness.Populate(first);
        using var second = d.Open(); first.Settings = first.Settings with { Revision = 2 };
        second.Refresh();
        Assert.Equal("CATALOGUE_CONFLICT", Assert.Throws<CatalogueStoreException>(() => second.Settings = second.Settings with { Revision = 3 }).Message);
    }

    private sealed class CountingProtector : ISecretProtector
    {
        public int Reads; public string ProtectorId => "test-counting";
        public byte[] Protect(ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("read only");
        public byte[] Unprotect(ReadOnlySpan<byte> bytes) { Reads++; return bytes.ToArray(); }
    }
    private sealed class Store : IDisposable
    {
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("autovpn-v3-reader-").FullName;
        internal string Path => System.IO.Path.Combine(Directory, "catalogue.sqlite");
        internal SqliteCatalogue Open() => SqliteCatalogue.Open(Path, new PassthroughSecretProtector());
        internal SqliteCatalogue Read() => SqliteCatalogue.OpenReadOnly(Path, new PassthroughSecretProtector());
        internal void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
