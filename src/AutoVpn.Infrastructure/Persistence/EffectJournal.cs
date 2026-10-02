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

    public static EffectJournal Open(string path)
    {
        string? quarantined = null;
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
        var removed = 0;
        foreach (var id in result.RemovedIds)
        {
            if (open.Any(effect => effect.Id == id))
            {
                MarkRemoved(id);
                removed++;
            }
        }

        var completed = result.Completed && removed == open.Count;
        return new JournalRecovery(completed, result.ReasonCode, null, open.Count - removed, removed);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static void Inspect(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header) != 16 || !header.SequenceEqual("SQLite format 3\0"u8))
            {
                throw new CatalogueStoreException("Effect journal is not a SQLite file.", MoveAside(path));
            }
        }

        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        probe.Open();
        using var version = probe.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        try
        {
            var value = version.ExecuteScalar() as string;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schema) || schema != SchemaVersion)
            {
                var newer = schema > SchemaVersion;
                throw new CatalogueStoreException(newer
                    ? "Effect journal was written by a newer AutoVPN. It was left untouched."
                    : "Effect journal schema is not supported. It was left untouched.");
            }
        }
        catch (SqliteException)
        {
            probe.Close();
            throw new CatalogueStoreException("Effect journal could not be read.", MoveAside(path));
        }

        using var integrity = probe.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var status = integrity.ExecuteScalar() as string;
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            probe.Close();
            throw new CatalogueStoreException("Effect journal failed integrity_check.", MoveAside(path));
        }
    }

    private static string MoveAside(string path)
    {
        var destination = path + ".quarantine-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        File.Move(path, destination);
        return destination;
    }
}
