using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Allegiance.Core.Identity;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Allegiance.Lobby.Identity;

/// <summary>Versioned, parameterized SQL shared by SQLite and PostgreSQL. No game-state persistence.</summary>
public sealed class IdentityStore
{
    private readonly IdentitySettings _settings;
    private readonly TimeProvider _clock;
    public IdentityStore(IdentitySettings settings, TimeProvider clock) { _settings = settings; _clock = clock; }
    public long Now => _clock.GetUtcNow().ToUnixTimeSeconds();
    public static string Secret() => JoinTokens.Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private DbConnection Open()
    {
        DbConnection c = _settings.Store == "Postgres" ? new NpgsqlConnection(_settings.ConnectionString) : new SqliteConnection(_settings.ConnectionString);
        c.Open();
        return c;
    }
    private static DbCommand Command(DbConnection c, string sql, params object?[] values)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++)
        {
            var p = cmd.CreateParameter(); p.ParameterName = "p" + i; p.Value = values[i] ?? DBNull.Value; cmd.Parameters.Add(p);
        }
        return cmd;
    }
    public int Execute(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); return cmd.ExecuteNonQuery();
    }
    public string[]? Row(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); using var reader = cmd.ExecuteReader();
        return reader.Read() ? Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray() : null;
    }
    public void Migrate()
    {
        if (_settings.Store == "Sqlite")
        {
            var path = new SqliteConnectionStringBuilder(_settings.ConnectionString).DataSource;
            if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        using var c = Open();
        // A PostgreSQL transaction advisory lock serializes DDL across lobby replicas.
        using var tx = c.BeginTransaction();
        if (_settings.Store == "Postgres")
        {
            using var gate = Command(c, "SELECT pg_advisory_xact_lock(808001)"); gate.Transaction = tx; gate.ExecuteNonQuery();
        }
        using var migration = Command(c, """
            CREATE TABLE IF NOT EXISTS identity_migrations (version INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, callsign TEXT NOT NULL, normalized TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS credentials (id TEXT PRIMARY KEY, account_id TEXT NOT NULL REFERENCES accounts(id), public_key TEXT NOT NULL, counter BIGINT NOT NULL);
            CREATE TABLE IF NOT EXISTS external_subjects (provider TEXT NOT NULL, subject TEXT NOT NULL, account_id TEXT NOT NULL REFERENCES accounts(id), PRIMARY KEY(provider, subject));
            CREATE TABLE IF NOT EXISTS sessions (hash TEXT PRIMARY KEY, account_id TEXT NOT NULL REFERENCES accounts(id), expires BIGINT NOT NULL);
            CREATE TABLE IF NOT EXISTS challenges (hash TEXT PRIMARY KEY, kind TEXT NOT NULL, data TEXT NOT NULL, expires BIGINT NOT NULL);
            CREATE TABLE IF NOT EXISTS devices (hash TEXT PRIMARY KEY, user_code TEXT NOT NULL UNIQUE, purpose TEXT NOT NULL, name TEXT NOT NULL, expires BIGINT NOT NULL, last_poll BIGINT NOT NULL, account_id TEXT);
            CREATE TABLE IF NOT EXISTS enrolled_servers (id TEXT PRIMARY KEY, secret_hash TEXT NOT NULL UNIQUE, operator_id TEXT NOT NULL REFERENCES accounts(id), name TEXT NOT NULL);
            INSERT INTO identity_migrations(version) VALUES(1) ON CONFLICT(version) DO NOTHING;
            -- P808b — career statistics and leaderboard (club-DB equivalent).
            CREATE TABLE IF NOT EXISTS career_meta (account_id TEXT PRIMARY KEY REFERENCES accounts(id), description TEXT NOT NULL, combat_rating REAL NOT NULL, updated_at BIGINT NOT NULL);
            CREATE TABLE IF NOT EXISTS career_by_civ (account_id TEXT NOT NULL REFERENCES accounts(id), civ SMALLINT NOT NULL, score REAL NOT NULL, rank SMALLINT NOT NULL, combat_rating REAL NOT NULL, kills INTEGER NOT NULL, deaths INTEGER NOT NULL, base_kills INTEGER NOT NULL, base_captures INTEGER NOT NULL, minutes INTEGER NOT NULL, wins INTEGER NOT NULL, losses INTEGER NOT NULL, games INTEGER NOT NULL, commander_wins INTEGER NOT NULL, commander_losses INTEGER NOT NULL, PRIMARY KEY(account_id, civ));
            CREATE TABLE IF NOT EXISTS match_results (match_id TEXT NOT NULL, account_id TEXT NOT NULL, civ SMALLINT NOT NULL, score REAL NOT NULL, combat_rating REAL NOT NULL, kills INTEGER NOT NULL, deaths INTEGER NOT NULL, base_kills INTEGER NOT NULL, base_captures INTEGER NOT NULL, minutes INTEGER NOT NULL, win INTEGER NOT NULL, loss INTEGER NOT NULL, commander_win INTEGER NOT NULL, commander_loss INTEGER NOT NULL, reported_at BIGINT NOT NULL, server_id TEXT NOT NULL, PRIMARY KEY(match_id, account_id));
            INSERT INTO identity_migrations(version) VALUES(2) ON CONFLICT(version) DO NOTHING;
            -- P820a — squads (native Zone Club squad database; SquadsScreen.cpp,
            -- ZoneSquad.cpp). wins/losses/score are native SQUAD_INFO display
            -- fields, all 0 until P820b ties squads into games.
            CREATE TABLE IF NOT EXISTS squads (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, tag TEXT NOT NULL, description TEXT NOT NULL, url TEXT NOT NULL, civ SMALLINT NOT NULL, created_at BIGINT NOT NULL, wins INTEGER NOT NULL DEFAULT 0, losses INTEGER NOT NULL DEFAULT 0, score INTEGER NOT NULL DEFAULT 0, leader_account_id TEXT NOT NULL REFERENCES accounts(id));
            CREATE TABLE IF NOT EXISTS squad_members (squad_id TEXT NOT NULL REFERENCES squads(id) ON DELETE CASCADE, account_id TEXT NOT NULL REFERENCES accounts(id), status TEXT NOT NULL, last_played BIGINT, joined_at BIGINT NOT NULL, PRIMARY KEY(squad_id, account_id));
            CREATE TABLE IF NOT EXISTS squad_log (squad_id TEXT NOT NULL REFERENCES squads(id) ON DELETE CASCADE, seq INTEGER NOT NULL, line TEXT NOT NULL, PRIMARY KEY(squad_id, seq));
            INSERT INTO identity_migrations(version) VALUES(3) ON CONFLICT(version) DO NOTHING;
            -- P820b — zone events (native WinTrek/zoneeventsscreen.cpp EventListing:
            -- date/time/name/description/sign-up URL/attached files). files is a
            -- newline-joined TEXT (native TList<ZString> m_listFiles); events are
            -- operator-managed, player read-only.
            CREATE TABLE IF NOT EXISTS events (id TEXT PRIMARY KEY, date TEXT NOT NULL, time TEXT NOT NULL, name TEXT NOT NULL, description TEXT NOT NULL, signup_url TEXT NOT NULL, files TEXT NOT NULL, created_at BIGINT NOT NULL);
            INSERT INTO identity_migrations(version) VALUES(4) ON CONFLICT(version) DO NOTHING;
            -- P855/SIM-0765 — one row per ingested squad game (the port of what
            -- the club DB's SquadReportGame proc records, FedSrv/srvqueries.h:173-194):
            -- the idempotence key that keeps a replayed match report from
            -- double-crediting standings; loser ids are ','-joined squad ids.
            CREATE TABLE IF NOT EXISTS squad_game_results (match_key TEXT PRIMARY KEY, winner_squad_id TEXT, loser_squad_ids TEXT NOT NULL, reported_at BIGINT NOT NULL);
            """);
        migration.Transaction = tx; migration.ExecuteNonQuery(); tx.Commit();
    }
    public IdentityAccount CreateAccount(string callsign, string? id = null)
    {
        if (!JoinTokens.ValidCallsign(callsign)) throw new ArgumentException("Callsign must be 1–24 printable Latin-1 characters, without outer spaces");
        id ??= Guid.NewGuid().ToString("N");
        Execute("INSERT INTO accounts(id,callsign,normalized) VALUES(@p0,@p1,@p2)", id, callsign, callsign.ToUpperInvariant());
        return new(id, callsign);
    }
    public IdentityAccount CreatePasskeyAccount(string id, string callsign, string credentialId, string publicKey, uint counter)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var account = Command(c, "INSERT INTO accounts(id,callsign,normalized) VALUES(@p0,@p1,@p2)", id, callsign, callsign.ToUpperInvariant());
        account.Transaction = tx; account.ExecuteNonQuery();
        using var credential = Command(c, "INSERT INTO credentials(id,account_id,public_key,counter) VALUES(@p0,@p1,@p2,@p3)", credentialId, id, publicKey, (long)counter);
        credential.Transaction = tx; credential.ExecuteNonQuery(); tx.Commit();
        return new(id, callsign);
    }
    public IdentityAccount ExternalAccount(string provider, string subject, string callsign)
    {
        var existing = Row("SELECT account_id FROM external_subjects WHERE provider=@p0 AND subject=@p1", provider, subject);
        if (existing is not null) return Account(existing[0])!;
        if (!JoinTokens.ValidCallsign(callsign)) throw new ArgumentException("Choose an unused callsign first");
        string id = Guid.NewGuid().ToString("N");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var account = Command(c, "INSERT INTO accounts(id,callsign,normalized) VALUES(@p0,@p1,@p2)", id, callsign, callsign.ToUpperInvariant());
        account.Transaction = tx; account.ExecuteNonQuery();
        using var link = Command(c, "INSERT INTO external_subjects(provider,subject,account_id) VALUES(@p0,@p1,@p2)", provider, subject, id);
        link.Transaction = tx; link.ExecuteNonQuery(); tx.Commit();
        return new(id, callsign);
    }
    public IdentityAccount? Account(string id)
    {
        var r = Row("SELECT id,callsign FROM accounts WHERE id=@p0", id); return r is null ? null : new(r[0], r[1]);
    }
    public string Session(string accountId)
    {
        string secret = Secret(); Execute("INSERT INTO sessions(hash,account_id,expires) VALUES(@p0,@p1,@p2)", Hash(secret), accountId, Now + 43200); return secret;
    }
    public IdentityAccount? Authenticate(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 128) return null;
        var r = Row("SELECT account_id FROM sessions WHERE hash=@p0 AND expires>@p1", Hash(secret), Now); return r is null ? null : Account(r[0]);
    }
    public void Revoke(string secret) => Execute("DELETE FROM sessions WHERE hash=@p0", Hash(secret));
    public void Prune()
    {
        Execute("DELETE FROM sessions WHERE expires<=@p0", Now);
        Execute("DELETE FROM challenges WHERE expires<=@p0", Now);
        Execute("DELETE FROM devices WHERE expires<=@p0", Now);
    }
    public string Challenge(string kind, string json)
    {
        Prune(); string secret = Secret();
        Execute("INSERT INTO challenges(hash,kind,data,expires) VALUES(@p0,@p1,@p2,@p3)", Hash(secret), kind, json, Now + 300); return secret;
    }
    public string? ConsumeChallenge(string secret, string kind)
    {
        // DELETE RETURNING is atomic on both supported engines.
        return Row("DELETE FROM challenges WHERE hash=@p0 AND kind=@p1 AND expires>@p2 RETURNING data", Hash(secret), kind, Now)?[0];
    }
    public DeviceAuthorization StartDevice(DeviceStartRequest req)
    {
        if (req.Purpose is not "player" and not "server" || req.Name is not { Length: >= 1 and <= 80 } || req.Name.Any(char.IsControl))
            throw new ArgumentException("Invalid device purpose/name");
        Prune(); string secret = Secret(); string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        Execute("INSERT INTO devices(hash,user_code,purpose,name,expires,last_poll) VALUES(@p0,@p1,@p2,@p3,@p4,0)", Hash(secret), code, req.Purpose, req.Name, Now + 300);
        return new(secret, code, _settings.PublicUrl + "/identity.html", 300, 5);
    }
    public string[]? Device(string code) => Row("SELECT purpose,name,account_id FROM devices WHERE user_code=@p0 AND expires>@p1", code, Now);
    public bool Approve(string code, string accountId)
    {
        var d = Device(code);
        if (d is null || d[0] == "server" && !_settings.OperatorAccountIds.Contains(accountId, StringComparer.Ordinal)) return false;
        return Execute("UPDATE devices SET account_id=@p0 WHERE user_code=@p1 AND account_id IS NULL AND expires>@p2", accountId, code, Now) == 1;
    }
    public DeviceResult Poll(string secret)
    {
        string hash = Hash(secret);
        // Enforce polling interval even for unapproved devices. One concurrent poll wins.
        if (Execute("UPDATE devices SET last_poll=@p0 WHERE hash=@p1 AND expires>@p0 AND last_poll<=@p2", Now, hash, Now - 5) == 0)
            return new(Row("SELECT hash FROM devices WHERE hash=@p0 AND expires>@p1", hash, Now) is null ? "expired" : "slow_down");
        var r = Row("DELETE FROM devices WHERE hash=@p0 AND account_id IS NOT NULL AND expires>@p1 RETURNING purpose,name,account_id", hash, Now);
        if (r is null) return new("pending");
        if (r[0] == "player") return new("approved", Session(r[2]), Account(r[2]));
        string id = Guid.NewGuid().ToString("N"), key = Secret();
        Execute("INSERT INTO enrolled_servers(id,secret_hash,operator_id,name) VALUES(@p0,@p1,@p2,@p3)", id, Hash(key), r[2], r[1]);
        return new("approved", ServerId: id, ServerSecret: key);
    }
    public string? VerifyServer(string? id, string? secret) => id is null || secret is null ? null :
        Row("SELECT id FROM enrolled_servers WHERE id=@p0 AND secret_hash=@p1", id, Hash(secret))?[0];

    /// <summary>
    /// P808c — the registered (account-held) callsigns, case-preserved. Game
    /// servers fetch this so an ANONYMOUS logon cannot take the name of a
    /// registered player (reserved-callsign protection, P808a follow-up).
    /// </summary>
    public string[] RegisteredCallsigns()
    {
        var names = new List<string>();
        using var c = Open();
        using var cmd = Command(c, "SELECT callsign FROM accounts");
        using var dr = cmd.ExecuteReader();
        while (dr.Read()) names.Add(dr.IsDBNull(0) ? "" : Convert.ToString(dr.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return names.ToArray();
    }
}
