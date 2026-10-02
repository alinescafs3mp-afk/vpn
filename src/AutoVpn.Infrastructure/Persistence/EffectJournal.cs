using System.Globalization;
using AutoVpn.Infrastructure.Broker;
using Microsoft.Data.Sqlite;

namespace AutoVpn.Infrastructure.Persistence;

public sealed record OwnedEffect(string Id, string Kind, string Detail);

public sealed record JournalRecovery(bool Completed, string? ReasonCode, string? QuarantinePath, int OpenEffects, int RemovedEffects);

public sealed class EffectJournal : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;

    public string? QuarantinedFrom { get; private set; }

    private EffectJournal(SqliteConnection connection, string? quarantinedFrom)
    {
        _connection = connection;
        QuarantinedFrom = quarantinedFrom;
    }

    public static string UnknownMarkerPath(string path)
    {
        return path + ".recovery-unknown";
    }

    public static bool HasUnknownMarker(string path)
    {
        return File.Exists(UnknownMarkerPath(path));
    }

    public static string PresenceMarkerPath(string path)
    {
        return path + ".journal-seen";
    }

    public static bool RequiresReconciliation(string path)
    {
        if (File.Exists(path))
        {
            return false;
        }

        if (HasUnknownMarker(path) || File.Exists(PresenceMarkerPath(path)))
        {
            return true;
        }

        if (File.Exists(path + "-wal") || File.Exists(path + "-shm") || File.Exists(path + "-journal"))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        if (!Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".quarantine-*").Any();
    }

    public static EffectJournal Open(string path)
    {
        var presence = PresenceMarkerPath(path);
        if (!File.Exists(presence))
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(presence, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        string? quarantined = null;
        var marker = UnknownMarkerPath(path);
        if (File.Exists(marker))
        {
            var recorded = File.ReadAllText(marker).Trim();
            quarantined = recorded.Length == 0 ? marker : recorded;
        }

        if (File.Exists(path))
        {
            try
            {
                Inspect(path);
            }
            catch (CatalogueStoreException ex) when (ex.QuarantinePath is not null)
            {
                quarantined = ex.QuarantinePath;
            }
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS effects (
                  id TEXT PRIMARY KEY,
                  kind TEXT NOT NULL,
                  detail TEXT NOT NULL,
                  created_utc TEXT NOT NULL,
                  removed_utc TEXT NULL
                );
                INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');
                """;
            schema.ExecuteNonQuery();
        }

        return new EffectJournal(connection, quarantined);
    }

    public void Record(OwnedEffect effect)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO effects(id, kind, detail, created_utc, removed_utc)
            VALUES ($id, $kind, $detail, $created, NULL);
            """;
        command.Parameters.AddWithValue("$id", effect.Id);
        command.Parameters.AddWithValue("$kind", effect.Kind);
        command.Parameters.AddWithValue("$detail", effect.Detail);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<OwnedEffect> OpenEffects()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, kind, detail FROM effects WHERE removed_utc IS NULL ORDER BY created_utc;";
        using var reader = command.ExecuteReader();
        var effects = new List<OwnedEffect>();
        while (reader.Read())
        {
            effects.Add(new OwnedEffect(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return effects;
    }

    public void MarkRemoved(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE effects SET removed_utc=$now WHERE id=$id AND removed_utc IS NULL;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public JournalRecovery Recover(INetworkGuard guard)
    {
        var open = OpenEffects();
        if (QuarantinedFrom is not null)
        {
            return new JournalRecovery(false, "JOURNAL_UNREADABLE", QuarantinedFrom, open.Count, 0);
        }

        var result = guard.Recover(open);
        var pending = open.Select(effect => effect.Id).ToHashSet(StringComparer.Ordinal);
        var removedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in result.RemovedIds)
        {
            if (pending.Contains(id) && removedIds.Add(id))
            {
                MarkRemoved(id);
            }
        }

        var completed = result.Completed && removedIds.Count == pending.Count;
        return new JournalRecovery(completed, result.ReasonCode, null, pending.Count - removedIds.Count, removedIds.Count);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static void Inspect(string path)
    {
        if (!HasSqliteHeader(path))
        {
            throw new CatalogueStoreException("Effect journal is not a SQLite file.", MoveAside(path));
        }

        var probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        string? quarantineReason = null;
        try
        {
            probe.Open();
            using (var version = probe.CreateCommand())
            {
                version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
                var value = version.ExecuteScalar() as string;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schema) || schema != SchemaVersion)
                {
                    var newer = schema > SchemaVersion;
                    throw new CatalogueStoreException(newer
                        ? "Effect journal was written by a newer AutoVPN. It was left untouched."
                        : "Effect journal schema is not supported. It was left untouched.");
                }
            }

            using var integrity = probe.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var status = integrity.ExecuteScalar() as string;
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                quarantineReason = "Effect journal failed integrity_check.";
            }
        }
        catch (SqliteException)
        {
            quarantineReason = "Effect journal could not be read.";
        }
        finally
        {
            probe.Dispose();
        }

        if (quarantineReason is not null)
        {
            throw new CatalogueStoreException(quarantineReason, MoveAside(path));
        }
    }

    private static bool HasSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static string MoveAside(string path)
    {
        var destination = path + ".quarantine-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        File.WriteAllText(UnknownMarkerPath(path), destination);
        MoveIfExists(path + "-wal", destination + "-wal");
        MoveIfExists(path + "-shm", destination + "-shm");
        MoveIfExists(path + "-journal", destination + "-journal");
        if (File.Exists(path))
        {
            File.Move(path, destination);
        }

        return destination;
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }
}
