using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ElanciaLocal;

internal sealed class AccountDatabase
{
    const int Iterations = 600_000;
    readonly string connectionString;
    static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(16);

    internal AccountDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 5
        }.ToString();
        using var db = Open();
        Execute(db, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY, login TEXT UNIQUE COLLATE NOCASE NOT NULL, salt BLOB, hash BLOB);
            CREATE TABLE IF NOT EXISTS characters(id INTEGER PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts(id), state TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS characters_account ON characters(account);
            CREATE TABLE IF NOT EXISTS migration(name TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS game_sessions(token_hash TEXT PRIMARY KEY,
                account TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                loopback_only INTEGER NOT NULL, expires_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS game_sessions_account ON game_sessions(account);
            """);
    }

    internal static bool ValidLogin(string? login) =>
        login is { Length: >=4 and <=24 } && login.All(char.IsAsciiLetterOrDigit);
    internal static bool ValidPassword(string? password) =>
        password is { Length: >=4 and <=128 } && password.All(char.IsAsciiLetterOrDigit);

    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        using var cmd = Command(db, tx, sql, values);
        return cmd.ExecuteNonQuery();
    }

    static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    internal bool Register(string login, string password)
    {
        if (!ValidLogin(login) || !ValidPassword(password)) return false;
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Derive(password, salt);
        using var db = Open();
        return Execute(db, null, "INSERT OR IGNORE INTO accounts VALUES($id,$login,$salt,$hash)",
            ("$id", Guid.NewGuid().ToString("N")), ("$login", login), ("$salt", salt), ("$hash", hash)) == 1;
    }

    internal string? Authenticate(string login, string password)
    {
        if (!ValidLogin(login) || password is not { Length: >=4 and <=128 }) return null;
        string? id = null;
        byte[]? salt = null, hash = null;
        using (var db = Open())
        using (var cmd = Command(db, null, "SELECT id,salt,hash FROM accounts WHERE login=$login", ("$login", login)))
        using (var row = cmd.ExecuteReader()) {
            if (row.Read() && !row.IsDBNull(1) && !row.IsDBNull(2)) {
                id = row.GetString(0);
                salt = (byte[])row.GetValue(1);
                hash = (byte[])row.GetValue(2);
            }
        }
        byte[] actual = Derive(password, salt ?? DummySalt);
        return hash != null && CryptographicOperations.FixedTimeEquals(hash, actual) ? id : null;
    }

    internal bool UpdateAccount(string id, string login, string? password)
    {
        if (!ValidLogin(login) || password is not null && !ValidPassword(password)) return false;
        using var db = Open();
        if (password is null)
            return Execute(db, null, "UPDATE OR IGNORE accounts SET login=$login WHERE id=$id",
                ("$login", login), ("$id", id)) == 1;
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Derive(password, salt);
        using var tx = db.BeginTransaction();
        if (Execute(db, tx, "UPDATE OR IGNORE accounts SET login=$login,salt=$salt,hash=$hash WHERE id=$id",
            ("$login", login), ("$id", id), ("$salt", salt), ("$hash", hash)) != 1) return false;
        Execute(db, tx, "DELETE FROM game_sessions WHERE account=$id", ("$id", id));
        tx.Commit();
        return true;
    }

    internal bool SaveGameSession(string hash, GameSession session, long now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        Execute(db, tx, "DELETE FROM game_sessions WHERE expires_at<=$now", ("$now", now));
        bool added = Execute(db, tx, """
            INSERT INTO game_sessions(token_hash,account,loopback_only,expires_at)
            SELECT $hash,$account,$local,$expires WHERE (SELECT COUNT(*) FROM game_sessions) < 2048
            """, ("$hash", hash), ("$account", session.Account), ("$local", session.LoopbackOnly),
            ("$expires", session.ExpiresAt)) == 1;
        tx.Commit();
        return added;
    }

    internal GameSession? FindGameSession(string hash, long now)
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT account,loopback_only,expires_at FROM game_sessions WHERE token_hash=$hash AND expires_at>$now
            """, ("$hash", hash), ("$now", now));
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetBoolean(1), row.GetInt64(2)) : null;
    }

    internal sealed record GameSession(string Account, bool LoopbackOnly, long ExpiresAt);

    internal AccountView[] Accounts()
    {
        using var db = Open();
        using var cmd = Command(db, null, "SELECT id,login,hash IS NOT NULL FROM accounts ORDER BY login");
        using var row = cmd.ExecuteReader();
        var accounts = new List<AccountView>();
        while (row.Read()) accounts.Add(new(row.GetString(0), row.GetString(1), row.GetBoolean(2)));
        return accounts.ToArray();
    }

    internal List<Character> LoadCharacters(string legacyFile)
    {
        using var db = Open();
        using (var tx = db.BeginTransaction()) {
            if (Execute(db, tx, "INSERT OR IGNORE INTO migration VALUES('characters.json')") == 1 && File.Exists(legacyFile)) {
                var legacy = JsonSerializer.Deserialize<List<Character>>(File.ReadAllText(legacyFile))
                    ?? throw new InvalidDataException("Invalid legacy character file");
                foreach (string id in legacy.Select(c => c.Account).Distinct(StringComparer.Ordinal))
                    Execute(db, tx, "INSERT INTO accounts(id,login) VALUES($id,$login)", ("$id", id), ("$login", id));
                foreach (var c in legacy) WriteCharacter(db, tx, c, migrate: true);
            }
            tx.Commit();
        }
        using var query = Command(db, null, "SELECT state FROM characters ORDER BY id");
        using var rows = query.ExecuteReader();
        var characters = new List<Character>();
        while (rows.Read()) characters.Add(JsonSerializer.Deserialize<Character>(rows.GetString(0))
            ?? throw new InvalidDataException("Invalid character row"));
        return characters;
    }

    static void WriteCharacter(SqliteConnection db, SqliteTransaction tx, Character c, bool migrate = false) =>
        Execute(db, tx, migrate
            ? "INSERT INTO characters VALUES($id,$account,$state)"
            : "INSERT INTO characters VALUES($id,$account,$state) ON CONFLICT(id) DO UPDATE SET account=excluded.account,state=excluded.state",
            ("$id", (long)c.Id), ("$account", c.Account), ("$state", JsonSerializer.Serialize(c)));

    internal void SaveCharacters(List<Character> previous, List<Character> next)
    {
        var old = previous.ToDictionary(c => c.Id);
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var c in next) {
            if (!old.Remove(c.Id, out var prior) || !ReferenceEquals(prior, c)) WriteCharacter(db, tx, c);
        }
        foreach (uint removed in old.Keys)
            Execute(db, tx, "DELETE FROM characters WHERE id=$id", ("$id", (long)removed));
        tx.Commit();
    }

    internal sealed record AccountView(string Id, string Login, bool Enabled);
}
