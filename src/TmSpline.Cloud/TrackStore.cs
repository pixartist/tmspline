using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace TmSpline.Cloud;

/// <summary>A track in a user's list (without its data).</summary>
public sealed record TrackInfo(string Id, string Name, long Version, DateTimeOffset Updated, string? Share);

/// <summary>A track with its data: the editor's track file as JSON text.</summary>
public sealed record TrackFile(string Id, string Name, long Version, DateTimeOffset Updated, string? Share, string Data);

/// <summary>A shared track as anyone logged in sees it.</summary>
public sealed record SharedTrack(string Share, string Name, string Owner, DateTimeOffset Updated, string Data);

public sealed record SaveResult(bool Saved, TrackInfo Track);

/// <summary>
/// Users, their tracks and share links in one SQLite file. Users are Authentik's subject ids; a track belongs to one
/// user, a share link shows the track's current state to every logged-in user until it is removed.
/// </summary>
public sealed class TrackStore
{
    private readonly string _connection;

    public TrackStore(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString();
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS users (
                sub TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS tracks (
                id TEXT PRIMARY KEY,
                owner TEXT NOT NULL REFERENCES users(sub),
                name TEXT NOT NULL,
                data TEXT NOT NULL,
                version INTEGER NOT NULL,
                created INTEGER NOT NULL,
                updated INTEGER NOT NULL,
                share TEXT UNIQUE);
            CREATE INDEX IF NOT EXISTS tracks_owner ON tracks(owner, updated);
            """);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        Execute(db, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>Records the user (and their current display name) on each sign-in or API call.</summary>
    public void Touch(string sub, string name)
    {
        using var db = Open();
        using var command = Command(db, """
            INSERT INTO users (sub, name, seen) VALUES ($sub, $name, $now)
            ON CONFLICT(sub) DO UPDATE SET name = excluded.name, seen = excluded.seen
            """, ("$sub", sub), ("$name", name), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        command.ExecuteNonQuery();
    }

    public List<TrackInfo> List(string owner)
    {
        using var db = Open();
        using var command = Command(db, "SELECT id, name, version, updated, share FROM tracks WHERE owner = $owner ORDER BY updated DESC",
            ("$owner", owner));
        using var reader = command.ExecuteReader();
        var list = new List<TrackInfo>();
        while (reader.Read())
            list.Add(new TrackInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Time(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return list;
    }

    public int Count(string owner)
    {
        using var db = Open();
        using var command = Command(db, "SELECT COUNT(*) FROM tracks WHERE owner = $owner", ("$owner", owner));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public TrackFile? Get(string owner, string id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT id, name, version, updated, share, data FROM tracks WHERE owner = $owner AND id = $id",
            ("$owner", owner), ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new TrackFile(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Time(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5))
            : null;
    }

    /// <summary>
    /// Creates or replaces the track. With <paramref name="baseVersion"/>, only when the stored track is still that
    /// version (another machine may have saved it meanwhile); otherwise not saved, and the stored track is returned.
    /// Null when the id belongs to another user.
    /// </summary>
    public SaveResult? Save(string owner, string id, string name, string data, long? baseVersion)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        long? version = null;
        string? currentOwner = null;
        using (var read = Command(db, "SELECT owner, version FROM tracks WHERE id = $id", ("$id", id)))
        {
            read.Transaction = transaction;
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                currentOwner = reader.GetString(0);
                version = reader.GetInt64(1);
            }
        }
        if (currentOwner is not null && currentOwner != owner) return null;
        if (version is { } current && baseVersion is { } expected && expected != current)
            return new SaveResult(false, Info(db, transaction, id));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var write = Command(db, version is null
                   ? "INSERT INTO tracks (id, owner, name, data, version, created, updated) VALUES ($id, $owner, $name, $data, 1, $now, $now)"
                   : "UPDATE tracks SET name = $name, data = $data, version = version + 1, updated = $now WHERE id = $id",
                   ("$id", id), ("$owner", owner), ("$name", name), ("$data", data), ("$now", now)))
        {
            write.Transaction = transaction;
            write.ExecuteNonQuery();
        }
        var info = Info(db, transaction, id);
        transaction.Commit();
        return new SaveResult(true, info);
    }

    private static TrackInfo Info(SqliteConnection db, SqliteTransaction transaction, string id)
    {
        using var command = Command(db, "SELECT id, name, version, updated, share FROM tracks WHERE id = $id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new TrackInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Time(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public bool Delete(string owner, string id)
    {
        using var db = Open();
        using var command = Command(db, "DELETE FROM tracks WHERE owner = $owner AND id = $id", ("$owner", owner), ("$id", id));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>The track's share id, made on first use; null when the track is not the user's.</summary>
    public string? Share(string owner, string id)
    {
        using var db = Open();
        using (var read = Command(db, "SELECT share FROM tracks WHERE owner = $owner AND id = $id", ("$owner", owner), ("$id", id)))
        {
            var existing = read.ExecuteScalar();
            if (existing is null) return null;
            if (existing is string s) return s;
        }
        var share = NewShareId();
        using var write = Command(db, "UPDATE tracks SET share = $share WHERE owner = $owner AND id = $id",
            ("$share", share), ("$owner", owner), ("$id", id));
        write.ExecuteNonQuery();
        return share;
    }

    public bool Unshare(string owner, string id)
    {
        using var db = Open();
        using var command = Command(db, "UPDATE tracks SET share = NULL WHERE owner = $owner AND id = $id AND share IS NOT NULL",
            ("$owner", owner), ("$id", id));
        return command.ExecuteNonQuery() > 0;
    }

    public SharedTrack? Shared(string share)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT t.share, t.name, u.name, t.updated, t.data FROM tracks t JOIN users u ON u.sub = t.owner WHERE t.share = $share
            """, ("$share", share));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SharedTrack(reader.GetString(0), reader.GetString(1), reader.GetString(2), Time(reader.GetInt64(3)), reader.GetString(4))
            : null;
    }

    /// <summary>12 characters from an unambiguous alphabet (about 60 bits): not guessable.</summary>
    private static string NewShareId()
    {
        const string alphabet = "23456789abcdefghjkmnpqrstuvwxyz";
        return string.Create(12, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }
}
