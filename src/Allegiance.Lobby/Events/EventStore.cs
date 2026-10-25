using System.Data.Common;
using System.Globalization;
using Allegiance.Lobby.Identity;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Allegiance.Lobby.Events;

/// <summary>
/// Persistent zone-events store for the lobby (P820b) — the modern counterpart
/// of the native zone-events feed the club client downloaded
/// (<c>WinTrek/zoneeventsscreen.cpp</c>). It persists events (date, time,
/// name, description, sign-up URL, attached files, created-at) in the lobby's
/// shared SQLite/PostgreSQL store (DDL in <see cref="IdentityStore.Migrate"/>)
/// and serves them in native <c>EventListing</c> field order. Operators manage
/// events through an authenticated HTTP endpoint; players read them only.
/// </summary>
/// <remarks>
/// Shares the same connection helpers and the same versioned store as
/// <see cref="SquadStore"/>/<see cref="IdentityStore"/> (identical engine,
/// same connection string). All reads/writes are parameterized. The attached
/// file list is persisted as a single <c>TEXT</c> column joined by newline
/// (native's <c>TList&lt;ZString&gt; m_listFiles</c>, zoneeventsscreen.cpp:64-72),
/// split back on newline on read. Every reader is closed before the next
/// command on the same connection (PostgreSQL refuses a new command while a
/// reader is open — the P820a lesson).
/// </remarks>
public sealed class EventStore
{
    private readonly IdentitySettings _settings;
    public EventStore(IdentitySettings settings) { _settings = settings; }

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
    private string[]? Row(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }
    private static string[] ReadRow(DbDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!).ToArray();

    private static string SqId() => Guid.NewGuid().ToString("N");

    private static string[] SplitFiles(string joined) => joined.Length == 0 ? [] : joined.Split('\n');

    /// <summary>All events ordered by date then time — the order the native
    /// events screen presents them (zoneeventsscreen.cpp columns
    /// <c>colDate</c>/<c>colTime</c>).</summary>
    public IReadOnlyList<EventContracts.ZoneEvent> List()
    {
        var rows = new List<EventContracts.ZoneEvent>();
        using var c = Open();
        using var cmd = Command(c, "SELECT id,date,time,name,description,signup_url,files,created_at FROM events ORDER BY date,time");
        using var dr = cmd.ExecuteReader();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            rows.Add(new EventContracts.ZoneEvent(r[0], r[1], r[2], r[3], r[4], r[5], SplitFiles(r[6]), long.Parse(r[7], CultureInfo.InvariantCulture)));
        }
        return rows;
    }

    /// <summary>Fetch a single event, or <c>null</c> if absent.</summary>
    public EventContracts.ZoneEvent? Get(string id)
    {
        var r = Row("SELECT id,date,time,name,description,signup_url,files,created_at FROM events WHERE id=@p0", id);
        return r is null ? null
            : new EventContracts.ZoneEvent(r[0], r[1], r[2], r[3], r[4], r[5], SplitFiles(r[6]), long.Parse(r[7], CultureInfo.InvariantCulture));
    }

    /// <summary>Create an event — native operator management (the club feed's
    /// authoring side). Returns an error string when validation fails.</summary>
    public EventContracts.EventActionResult Create(EventContracts.CreateEventRequest req, long now)
    {
        string? err = Validate(req.Date, req.Time, req.Name, req.Description, req.SignUpUrl, req.Files);
        if (err is not null) return EventContracts.EventActionResult.Fail(err);
        string id = SqId();
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var ins = Command(c, """
            INSERT INTO events(id,date,time,name,description,signup_url,files,created_at)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)
            """, id, req.Date.Trim(), req.Time.Trim(), req.Name.Trim(), req.Description.Trim(), req.SignUpUrl.Trim(),
            string.Join("\n", req.Files), now))
        { ins.Transaction = tx; ins.ExecuteNonQuery(); }
        tx.Commit();
        return EventContracts.EventActionResult.Success;
    }

    /// <summary>Edit (full replace) an existing event — operator only.
    /// Returns <c>false</c> when the event does not exist.</summary>
    public EventContracts.EventActionResult Update(string id, EventContracts.UpdateEventRequest req, long now)
    {
        string? err = Validate(req.Date, req.Time, req.Name, req.Description, req.SignUpUrl, req.Files);
        if (err is not null) return EventContracts.EventActionResult.Fail(err);
        using var c = Open(); using var tx = c.BeginTransaction();
        using var upd = Command(c, """
            UPDATE events SET date=@p0,time=@p1,name=@p2,description=@p3,signup_url=@p4,files=@p5 WHERE id=@p6
            """, req.Date.Trim(), req.Time.Trim(), req.Name.Trim(), req.Description.Trim(), req.SignUpUrl.Trim(),
            string.Join("\n", req.Files), id);
        upd.Transaction = tx;
        int affected = upd.ExecuteNonQuery();
        tx.Commit();
        return affected > 0 ? EventContracts.EventActionResult.Success : EventContracts.EventActionResult.Fail("Event not found.");
    }

    /// <summary>Delete an event — operator only. Returns <c>false</c> when absent.</summary>
    public bool Delete(string id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var del = Command(c, "DELETE FROM events WHERE id=@p0", id);
        del.Transaction = tx;
        int affected = del.ExecuteNonQuery();
        tx.Commit();
        return affected > 0;
    }

    private static string? Validate(string date, string time, string name, string description, string signUpUrl, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(date)) return "Events need a date.";
        if (string.IsNullOrWhiteSpace(name)) return "Events need a name.";
        if (name.Trim().Length > EventContracts.MaxName) return $"Event name must be ≤{EventContracts.MaxName} characters.";
        if (description.Trim().Length > EventContracts.MaxDescription) return "Event description too long.";
        if (signUpUrl.Trim().Length > EventContracts.MaxUrl) return "Sign-up URL too long.";
        if (files.Count > EventContracts.MaxFiles) return "Too many attached files.";
        foreach (var f in files) if (f.Contains('\n') || f.Contains('\r')) return "Attached file names cannot contain newlines.";
        return null;
    }
}
