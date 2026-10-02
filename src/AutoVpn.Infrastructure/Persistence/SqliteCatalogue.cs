using System.Globalization;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Domain;
using Microsoft.Data.Sqlite;

namespace AutoVpn.Infrastructure.Persistence;

/// <summary>
/// Durable catalogue. Subscription bodies are not stored. Node credentials are
/// passed through <see cref="ISecretProtector"/> before they touch the file.
/// The broker effect journal is a different database.
/// </summary>
public sealed class SqliteCatalogue : ICatalogue, IDisposable
{
    public const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly ISecretProtector _protector;
    private MemoryCatalogue _memory = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Json, byte[] Blob)> _secrets = new(StringComparer.Ordinal);
    private long _revision;

    public string? QuarantinedFrom { get; }

    private SqliteCatalogue(SqliteConnection connection, ISecretProtector protector, string? quarantinedFrom)
    {
        _connection = connection;
        _protector = protector;
        QuarantinedFrom = quarantinedFrom;
        try
        {
            Load();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static SqliteCatalogue Open(string path, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(protector);
        string? quarantined = null;
        if (File.Exists(path))
        {
            if (!HasSqliteHeader(path))
            {
                quarantined = MoveAside(path);
            }
            else
            {
                quarantined = InspectExisting(path) ?? quarantined;
            }
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        if (!FileHasSchema(connection))
        {
            CreateSchema(connection);
        }

        return new SqliteCatalogue(connection, protector, quarantined);
    }

    public long NetworkEpoch => _memory.NetworkEpoch;

    public ProductSettings Settings
    {
        get => _memory.Settings;
        set
        {
            var error = value.Validate();
            if (error is not null)
            {
                throw new CatalogueStoreException(error);
            }

            Commit(copy => copy.Settings = value);
        }
    }

    public IReadOnlyList<CatalogueNode> Nodes => _memory.Nodes;

    public void SetNetworkEpoch(long epoch)
    {
        Commit(copy => copy.SetNetworkEpoch(epoch));
    }

    public void ApplySnapshot(SnapshotCommit commit)
    {
        Commit(copy => copy.ApplySnapshot(commit));
    }

    public void ApplyAssessment(string nodeId, AssessmentSnapshot assessment)
    {
        Commit(copy => copy.ApplyAssessment(nodeId, assessment));
    }

    public int EvictOverflow(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var next = _memory.Copy();
            var removed = next.EvictOverflow(nowUtc);
            if (removed > 0)
            {
                Save(next);
                _memory = next;
            }

            return removed;
        }
    }

    public bool TrySetFavorite(string nodeId, bool favorite)
    {
        lock (_gate)
        {
            var next = _memory.Copy();
            if (!next.TrySetFavorite(nodeId, favorite))
            {
                return false;
            }

            Save(next);
            _memory = next;
            return true;
        }
    }

    public bool TrySetExcluded(string nodeId, bool excluded)
    {
        lock (_gate)
        {
            var next = _memory.Copy();
            if (!next.TrySetExcluded(nodeId, excluded))
            {
                return false;
            }

            Save(next);
            _memory = next;
            return true;
        }
    }

    public void SetActiveNode(string? nodeId)
    {
        Commit(copy => copy.SetActiveNode(nodeId));
    }

    private void Commit(Action<MemoryCatalogue> mutate)
    {
        lock (_gate)
        {
            var next = _memory.Copy();
            mutate(next);
            Save(next);
            _memory = next;
        }
    }

    public IReadOnlyList<CatalogueNode> Eligible(EligibilityContext context)
    {
        return _memory.Eligible(context);
    }

    public void BackupTo(string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            throw new IOException("Backup destination already exists.");
        }

        lock (_gate)
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            destination.Open();
            _connection.BackupDatabase(destination);
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void Load()
    {
        var settings = new ProductSettings();
        long epoch = 1;
        using (var meta = _connection.CreateCommand())
        {
            meta.CommandText = "SELECT key, value FROM meta;";
            using var reader = meta.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                if (key == "network_epoch" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    epoch = parsed;
                }
                else if (key == "catalogue_revision" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
                {
                    _revision = revision;
                }
                else if (key == "settings")
                {
                    settings = JsonSerializer.Deserialize<ProductSettings>(value, StoredJson.Options) ?? new ProductSettings();
                }
            }
        }

        if (settings.SchemaVersion > ProductLimits.SettingsSchemaVersion)
        {
            throw new CatalogueStoreException("Настройки созданы более новой версией AutoVPN. Откат не выполняется.");
        }

        var nodes = new List<CatalogueNode>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT node_id, digest, semantics_blob, label, country, favorite, excluded, active,
                       first_seen, last_seen, assessment_json, policy_reason
                FROM nodes;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var protectedSemantics = (byte[])reader.GetValue(2);
                var semanticsJson = Encoding.UTF8.GetString(_protector.Unprotect(protectedSemantics));
                _secrets[reader.GetString(0)] = (semanticsJson, protectedSemantics);
                var semantics = JsonSerializer.Deserialize<NodeSemantics>(semanticsJson, StoredJson.Options)
                    ?? throw new CatalogueStoreException("Stored node semantics could not be read.");
                var node = new CatalogueNode
                {
                    NodeId = reader.GetString(0),
                    Digest = reader.GetString(1),
                    Semantics = semantics,
                    Label = reader.GetString(3),
                    AdvertisedCountry = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Favorite = reader.GetInt32(5) != 0,
                    Excluded = reader.GetInt32(6) != 0,
                    ActiveSession = reader.GetInt32(7) != 0,
                    FirstSeenUtc = ParseTime(reader.GetString(8)),
                    LastSeenUtc = ParseTime(reader.GetString(9)),
                    Assessment = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<AssessmentSnapshot>(reader.GetString(10), StoredJson.Options),
                    PolicyReason = reader.IsDBNull(11) ? null : reader.GetString(11),
                };
                MemoryCatalogue.ReconcileStoredDigest(node);
                nodes.Add(node);
            }
        }

        using (var membership = _connection.CreateCommand())
        {
            membership.CommandText = "SELECT node_id, artifact_id, family_id FROM artifact_membership;";
            using var reader = membership.ExecuteReader();
            while (reader.Read())
            {
                var node = nodes.FirstOrDefault(item => item.NodeId == reader.GetString(0));
                node?.ArtifactFamilies.Add(reader.GetString(1), reader.GetString(2));
            }
        }

        using (var families = _connection.CreateCommand())
        {
            families.CommandText = "SELECT node_id, family_id, current FROM family_membership;";
            using var reader = families.ExecuteReader();
            while (reader.Read())
            {
                var node = nodes.FirstOrDefault(item => item.NodeId == reader.GetString(0));
                if (node is null)
                {
                    continue;
                }

                var set = reader.GetInt32(2) != 0 ? node.CurrentFamilies : node.HistoricalFamilies;
                set.Add(reader.GetString(1));
            }
        }

        _memory.Restore(epoch, settings, nodes);
    }

    private void Save(MemoryCatalogue source)
    {
        using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        long stored = 0;
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT value FROM meta WHERE key='catalogue_revision';";
            var current = read.ExecuteScalar() as string;
            if (current is not null && long.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                stored = parsed;
            }
        }

        if (stored != _revision)
        {
            throw new CatalogueStoreException("CATALOGUE_CONFLICT");
        }

        if (TryUpdateInPlace(source, transaction))
        {
            transaction.Commit();
            _revision++;
            return;
        }

        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM artifact_membership; DELETE FROM family_membership; DELETE FROM nodes; DELETE FROM meta;";
            clear.ExecuteNonQuery();
        }

        using (var meta = _connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $value);";
            var key = meta.CreateParameter();
            key.ParameterName = "$key";
            var value = meta.CreateParameter();
            value.ParameterName = "$value";
            meta.Parameters.Add(key);
            meta.Parameters.Add(value);
            void Put(string name, string text)
            {
                key.Value = name;
                value.Value = text;
                meta.ExecuteNonQuery();
            }

            Put("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            Put("catalogue_revision", (_revision + 1).ToString(CultureInfo.InvariantCulture));
            Put("network_epoch", source.NetworkEpoch.ToString(CultureInfo.InvariantCulture));
            Put("protector", _protector.ProtectorId);
            Put("settings", JsonSerializer.Serialize(source.Settings, StoredJson.Options));
        }

        foreach (var node in source.Nodes)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO nodes(node_id, digest, semantics_blob, label, country, favorite, excluded, active,
                                  first_seen, last_seen, assessment_json, policy_reason)
                VALUES ($id, $digest, $semantics, $label, $country, $favorite, $excluded, $active,
                        $first, $last, $assessment, $reason);
                """;
            var json = JsonSerializer.Serialize(node.Semantics, StoredJson.Options);
            var semantics = ProtectedSemantics(node.NodeId, json);
            insert.Parameters.AddWithValue("$id", node.NodeId);
            insert.Parameters.AddWithValue("$digest", node.Digest);
            insert.Parameters.Add("$semantics", SqliteType.Blob).Value = semantics;
            insert.Parameters.AddWithValue("$label", node.Label);
            insert.Parameters.AddWithValue("$country", (object?)node.AdvertisedCountry ?? DBNull.Value);
            insert.Parameters.AddWithValue("$favorite", node.Favorite ? 1 : 0);
            insert.Parameters.AddWithValue("$excluded", node.Excluded ? 1 : 0);
            insert.Parameters.AddWithValue("$active", node.ActiveSession ? 1 : 0);
            insert.Parameters.AddWithValue("$first", node.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$last", node.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$assessment", node.Assessment is null ? DBNull.Value : JsonSerializer.Serialize(node.Assessment, StoredJson.Options));
            insert.Parameters.AddWithValue("$reason", (object?)node.PolicyReason ?? DBNull.Value);
            insert.ExecuteNonQuery();

            foreach (var pair in node.ArtifactFamilies)
            {
                using var artifact = _connection.CreateCommand();
                artifact.Transaction = transaction;
                artifact.CommandText = "INSERT INTO artifact_membership(node_id, artifact_id, family_id) VALUES ($id, $artifact, $family);";
                artifact.Parameters.AddWithValue("$id", node.NodeId);
                artifact.Parameters.AddWithValue("$artifact", pair.Key);
                artifact.Parameters.AddWithValue("$family", pair.Value);
                artifact.ExecuteNonQuery();
            }

            void WriteFamily(string family, bool current)
            {
                using var familyCommand = _connection.CreateCommand();
                familyCommand.Transaction = transaction;
                familyCommand.CommandText = "INSERT INTO family_membership(node_id, family_id, current) VALUES ($id, $family, $current);";
                familyCommand.Parameters.AddWithValue("$id", node.NodeId);
                familyCommand.Parameters.AddWithValue("$family", family);
                familyCommand.Parameters.AddWithValue("$current", current ? 1 : 0);
                familyCommand.ExecuteNonQuery();
            }

            foreach (var family in node.CurrentFamilies)
            {
                WriteFamily(family, true);
            }

            foreach (var family in node.HistoricalFamilies)
            {
                WriteFamily(family, false);
            }
        }

        transaction.Commit();
        _revision++;
        var live = source.Nodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _secrets.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _secrets.Remove(stale);
        }
    }

    private bool TryUpdateInPlace(MemoryCatalogue source, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        if (!SameIdentity(_memory, source))
        {
            return false;
        }

        using (var count = _connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM meta WHERE key='catalogue_revision';";
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                return false;
            }
        }

        PutMeta(transaction, "catalogue_revision", (_revision + 1).ToString(CultureInfo.InvariantCulture));
        PutMeta(transaction, "network_epoch", source.NetworkEpoch.ToString(CultureInfo.InvariantCulture));
        PutMeta(transaction, "settings", JsonSerializer.Serialize(source.Settings, StoredJson.Options));
        foreach (var node in source.Nodes)
        {
            var previous = _memory.Nodes.First(item => item.NodeId == node.NodeId);
            if (!MutableDiffers(previous, node))
            {
                continue;
            }

            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE nodes
                SET digest=$digest, label=$label, country=$country, favorite=$favorite, excluded=$excluded, active=$active,
                    first_seen=$first, last_seen=$last, assessment_json=$assessment, policy_reason=$reason
                WHERE node_id=$id;
                """;
            update.Parameters.AddWithValue("$id", node.NodeId);
            update.Parameters.AddWithValue("$digest", node.Digest);
            update.Parameters.AddWithValue("$label", node.Label);
            update.Parameters.AddWithValue("$country", (object?)node.AdvertisedCountry ?? DBNull.Value);
            update.Parameters.AddWithValue("$favorite", node.Favorite ? 1 : 0);
            update.Parameters.AddWithValue("$excluded", node.Excluded ? 1 : 0);
            update.Parameters.AddWithValue("$active", node.ActiveSession ? 1 : 0);
            update.Parameters.AddWithValue("$first", node.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$last", node.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$assessment", node.Assessment is null ? DBNull.Value : JsonSerializer.Serialize(node.Assessment, StoredJson.Options));
            update.Parameters.AddWithValue("$reason", (object?)node.PolicyReason ?? DBNull.Value);
            update.ExecuteNonQuery();
        }

        return true;
    }

    private void PutMeta(Microsoft.Data.Sqlite.SqliteTransaction transaction, string name, string text)
    {
        using var meta = _connection.CreateCommand();
        meta.Transaction = transaction;
        meta.CommandText = "UPDATE meta SET value=$value WHERE key=$key;";
        meta.Parameters.AddWithValue("$key", name);
        meta.Parameters.AddWithValue("$value", text);
        meta.ExecuteNonQuery();
    }

    private byte[] ProtectedSemantics(string nodeId, string json)
    {
        if (_secrets.TryGetValue(nodeId, out var cached) && cached.Json == json)
        {
            return cached.Blob;
        }

        var blob = _protector.Protect(Encoding.UTF8.GetBytes(json));
        _secrets[nodeId] = (json, blob);
        return blob;
    }

    private static bool SameIdentity(MemoryCatalogue left, MemoryCatalogue right)
    {
        if (left.Nodes.Count != right.Nodes.Count)
        {
            return false;
        }

        var previous = left.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var node in right.Nodes)
        {
            if (!previous.TryGetValue(node.NodeId, out var stored))
            {
                return false;
            }

            if (JsonSerializer.Serialize(stored.Semantics, StoredJson.Options) != JsonSerializer.Serialize(node.Semantics, StoredJson.Options))
            {
                return false;
            }

            if (!stored.ArtifactFamilies.OrderBy(pair => pair.Key, StringComparer.Ordinal).SequenceEqual(node.ArtifactFamilies.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                || !stored.CurrentFamilies.OrderBy(family => family, StringComparer.Ordinal).SequenceEqual(node.CurrentFamilies.OrderBy(family => family, StringComparer.Ordinal))
                || !stored.HistoricalFamilies.OrderBy(family => family, StringComparer.Ordinal).SequenceEqual(node.HistoricalFamilies.OrderBy(family => family, StringComparer.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MutableDiffers(CatalogueNode left, CatalogueNode right)
    {
        var leftAssessment = left.Assessment is null ? null : JsonSerializer.Serialize(left.Assessment, StoredJson.Options);
        var rightAssessment = right.Assessment is null ? null : JsonSerializer.Serialize(right.Assessment, StoredJson.Options);
        return left.Digest != right.Digest
            || left.Label != right.Label
            || left.AdvertisedCountry != right.AdvertisedCountry
            || left.Favorite != right.Favorite
            || left.Excluded != right.Excluded
            || left.ActiveSession != right.ActiveSession
            || left.PolicyReason != right.PolicyReason
            || left.FirstSeenUtc != right.FirstSeenUtc
            || left.LastSeenUtc != right.LastSeenUtc
            || leftAssessment != rightAssessment;
    }

    private static string? InspectExisting(string path)
    {
        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        probe.Open();
        if (!FileHasSchema(probe))
        {
            if (HasUserTables(probe))
            {
                throw new CatalogueStoreException("Catalogue file has tables but no supported schema. It was left untouched.");
            }

            return null;
        }

        var version = ReadVersion(probe);
        if (version > SchemaVersion)
        {
            throw new CatalogueStoreException("Catalogue was written by a newer AutoVPN. Downgrade is refused.");
        }

        if (version < SchemaVersion)
        {
            throw new CatalogueStoreException("Catalogue schema is older than this build. Automatic downgrade is refused.");
        }

        using var integrity = probe.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var status = integrity.ExecuteScalar() as string;
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            probe.Close();
            return MoveAside(path);
        }

        return null;
    }

    private static bool FileHasSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='meta';";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool HasUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        var value = command.ExecuteScalar() as string;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 0;
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS nodes (
              node_id TEXT PRIMARY KEY,
              digest TEXT NOT NULL UNIQUE,
              semantics_blob BLOB NOT NULL,
              label TEXT NOT NULL,
              country TEXT NULL,
              favorite INTEGER NOT NULL,
              excluded INTEGER NOT NULL,
              active INTEGER NOT NULL,
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL,
              assessment_json TEXT NULL,
              policy_reason TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS artifact_membership (
              node_id TEXT NOT NULL,
              artifact_id TEXT NOT NULL,
              family_id TEXT NOT NULL,
              PRIMARY KEY (node_id, artifact_id),
              FOREIGN KEY (node_id) REFERENCES nodes(node_id)
            );
            CREATE TABLE IF NOT EXISTS family_membership (
              node_id TEXT NOT NULL,
              family_id TEXT NOT NULL,
              current INTEGER NOT NULL,
              PRIMARY KEY (node_id, family_id),
              FOREIGN KEY (node_id) REFERENCES nodes(node_id)
            );
            INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '1');
            """;
        command.ExecuteNonQuery();
    }

    private static bool HasSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static string MoveAside(string path)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var destination = path + ".quarantine-" + stamp;
        File.Move(path, destination);
        MoveIfExists(path + "-wal", destination + "-wal");
        MoveIfExists(path + "-shm", destination + "-shm");
        return destination;
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    private static DateTimeOffset ParseTime(string text)
    {
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
