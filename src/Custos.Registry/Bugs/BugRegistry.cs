using System.Globalization;
using Custos.Core.Util;
using Microsoft.Data.Sqlite;

namespace Custos.Registry.Bugs;

/// <summary>
/// 追記だけの入口（設計書 第20.7節）。開発AIと修正AIには、これだけを使わせる。
/// 一覧、検索、読み出しは持たない（書けるが読めない）。
/// </summary>
public interface IBugIntake
{
    /// <summary>不具合を1件登録し、振った ID を返す。</summary>
    string Add(NewBug bug);
}

/// <summary>登録簿を開く。</summary>
public static class BugRegistry
{
    /// <summary>追記だけの入口を開く。</summary>
    public static IBugIntake OpenIntake(string dbPath) => new BugIntake(new BugStore(dbPath));

    /// <summary>フレームワーク専用の管理の入口を開く。</summary>
    public static BugRegistryAdmin OpenAdmin(string dbPath) => new(new BugStore(dbPath));
}

/// <summary>追記だけの入口の実装。</summary>
internal sealed class BugIntake : IBugIntake
{
    private readonly BugStore _store;

    internal BugIntake(BugStore store) => _store = store;

    public string Add(NewBug bug) => _store.Insert(bug);
}

/// <summary>
/// フレームワーク専用の管理の入口：読み出し、自動で埋める項目の更新、結び付け、書き出し。
/// 更新はすべて変更履歴に追記して残す。
/// </summary>
public sealed class BugRegistryAdmin
{
    private readonly BugStore _store;

    internal BugRegistryAdmin(BugStore store) => _store = store;

    /// <summary>登録簿のファイル。</summary>
    public string DbPath => _store.Path;

    /// <summary>不具合を1件登録する。</summary>
    public string Add(NewBug bug) => _store.Insert(bug);

    /// <summary>ID が登録されているか。</summary>
    public bool Exists(string id) => _store.Get(id) != null;

    /// <summary>1件を読む。</summary>
    public BugRecord? Get(string id) => _store.Get(id);

    /// <summary>すべてを ID の順に読む。</summary>
    public IReadOnlyList<BugRecord> List() => _store.List();

    /// <summary>
    /// ID があればそれを返し、なければ template の内容でその ID の記録を作る。
    /// id が null なら新しい ID を振って作る（fix のコミットでバグIDがない場合。第20.3節）。
    /// </summary>
    public (string Id, bool Created) EnsureExists(string? id, NewBug template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (id != null && _store.Get(id) != null)
        {
            return (id, false);
        }

        var created = _store.Insert(template with { Id = id });
        return (created, true);
    }

    /// <summary>自動で埋める項目を更新する。変わった項目ごとに変更履歴を残す。</summary>
    public void Update(string id, BugUpdate update, string actor, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        _store.Update(id, update, actor, reason);
    }

    /// <summary>校正で使ったことを記録する（第19.8節。カンマ区切りで追記し、重複は足さない）。</summary>
    public void MarkUsedIn(string id, string calibrationId, string actor)
    {
        ArgumentNullException.ThrowIfNull(calibrationId);
        _store.AppendUsedIn(id, calibrationId, actor);
    }

    /// <summary>判定結果、レポート、修正コミット、再生手順などと結び付ける。</summary>
    public void Link(string id, string kind, string reference) => _store.Link(id, kind, reference);

    /// <summary>結び付きを読む。</summary>
    public IReadOnlyList<BugLink> Links(string id) => _store.Links(id);

    /// <summary>変更履歴を読む（id が null ならすべて）。</summary>
    public IReadOnlyList<BugChange> History(string? id = null) => _store.History(id);
}

/// <summary>SQLite の読み書き。接続は操作ごとに開いて閉じる（複数のプロセスから使えるようにする）。</summary>
internal sealed class BugStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS bugs (
          id            TEXT PRIMARY KEY,
          found_at      TEXT NOT NULL,
          found_by      TEXT NOT NULL,
          found_stage   TEXT NOT NULL,
          category      TEXT,
          reach         INTEGER,
          visibility    TEXT,
          severity      TEXT,
          introduced_in TEXT,
          fixed_in      TEXT,
          confirm_ref   TEXT,
          revivable     INTEGER,
          used_in       TEXT
        );
        CREATE TABLE IF NOT EXISTS bug_details (
          bug_id TEXT PRIMARY KEY REFERENCES bugs(id),
          title  TEXT NOT NULL,
          detail TEXT,
          source TEXT
        );
        CREATE TABLE IF NOT EXISTS bug_changes (
          seq       INTEGER PRIMARY KEY AUTOINCREMENT,
          bug_id    TEXT NOT NULL,
          at        TEXT NOT NULL,
          field     TEXT NOT NULL,
          old_value TEXT,
          new_value TEXT,
          actor     TEXT NOT NULL,
          reason    TEXT
        );
        CREATE TABLE IF NOT EXISTS bug_links (
          seq       INTEGER PRIMARY KEY AUTOINCREMENT,
          bug_id    TEXT NOT NULL,
          kind      TEXT NOT NULL,
          reference TEXT NOT NULL,
          at        TEXT NOT NULL,
          UNIQUE (bug_id, kind, reference)
        );
        CREATE TRIGGER IF NOT EXISTS bugs_no_delete BEFORE DELETE ON bugs
          BEGIN SELECT RAISE(ABORT, 'bugs is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS bugs_fixed_columns BEFORE UPDATE OF id, found_at, found_by, found_stage ON bugs
          BEGIN SELECT RAISE(ABORT, 'id, found_at, found_by and found_stage cannot be changed'); END;
        CREATE TRIGGER IF NOT EXISTS details_no_delete BEFORE DELETE ON bug_details
          BEGIN SELECT RAISE(ABORT, 'bug_details is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS details_no_update BEFORE UPDATE ON bug_details
          BEGIN SELECT RAISE(ABORT, 'bug_details is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS changes_no_update BEFORE UPDATE ON bug_changes
          BEGIN SELECT RAISE(ABORT, 'bug_changes is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS changes_no_delete BEFORE DELETE ON bug_changes
          BEGIN SELECT RAISE(ABORT, 'bug_changes is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS links_no_delete BEFORE DELETE ON bug_links
          BEGIN SELECT RAISE(ABORT, 'bug_links is append-only'); END;
        """;

    private const string SelectColumns = """
        SELECT b.id, b.found_at, b.found_by, b.found_stage, b.category, b.reach, b.visibility, b.severity,
               b.introduced_in, b.fixed_in, b.confirm_ref, b.revivable, b.used_in, d.title, d.detail, d.source
        FROM bugs b LEFT JOIN bug_details d ON d.bug_id = b.id
        """;

    private const string SelectOne = SelectColumns + " WHERE b.id = $id";
    private const string SelectAll = SelectColumns + " ORDER BY length(b.id), b.id";

    internal BugStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    internal string Path { get; }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string FormatTime(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal string Insert(NewBug bug)
    {
        ArgumentNullException.ThrowIfNull(bug);
        var problems = BugValues.Validate(bug);
        if (problems.Count > 0)
        {
            throw new CustosException("登録簿に記録できません：\n  " + string.Join("\n  ", problems));
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        string id;
        if (bug.Id != null)
        {
            if (ReadOne(connection, transaction, bug.Id) != null)
            {
                throw new CustosException($"その ID はすでに登録されています：{bug.Id}");
            }

            id = bug.Id;
        }
        else
        {
            id = BugValues.FormatId(MaxNumber(connection, transaction) + 1);
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO bugs (id, found_at, found_by, found_stage, category, reach, visibility, severity,
                                  introduced_in, fixed_in, confirm_ref, revivable, used_in)
                VALUES ($id, $found_at, $found_by, $found_stage, $category, $reach, $visibility, $severity,
                        NULL, NULL, NULL, NULL, NULL)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$found_at", FormatTime(bug.FoundAt ?? DateTimeOffset.UtcNow));
            insert.Parameters.AddWithValue("$found_by", bug.FoundBy);
            insert.Parameters.AddWithValue("$found_stage", bug.FoundStage);
            insert.Parameters.AddWithValue("$category", (object?)bug.Category ?? DBNull.Value);
            insert.Parameters.AddWithValue("$reach", (object?)bug.Reach ?? DBNull.Value);
            insert.Parameters.AddWithValue("$visibility", (object?)bug.Visibility ?? DBNull.Value);
            insert.Parameters.AddWithValue("$severity", (object?)bug.Severity ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        using (var details = connection.CreateCommand())
        {
            details.Transaction = transaction;
            details.CommandText = "INSERT INTO bug_details (bug_id, title, detail, source) VALUES ($id, $title, $detail, $source)";
            details.Parameters.AddWithValue("$id", id);
            details.Parameters.AddWithValue("$title", bug.Title.Trim());
            details.Parameters.AddWithValue("$detail", (object?)bug.Detail ?? DBNull.Value);
            details.Parameters.AddWithValue("$source", (object?)bug.Source ?? DBNull.Value);
            details.ExecuteNonQuery();
        }

        transaction.Commit();
        return id;
    }

    private static int MaxNumber(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM bugs";
        using var reader = command.ExecuteReader();
        var max = 0;
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (BugValues.IsValidId(id))
            {
                max = Math.Max(max, BugValues.NumberOf(id));
            }
        }

        return max;
    }

    internal BugRecord? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var connection = Open();
        return ReadOne(connection, null, id);
    }

    private static BugRecord? ReadOne(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectOne;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    internal IReadOnlyList<BugRecord> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAll;
        using var reader = command.ExecuteReader();
        var list = new List<BugRecord>();
        while (reader.Read())
        {
            list.Add(ReadRecord(reader));
        }

        return list;
    }

    private static BugRecord ReadRecord(SqliteDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
        var revivable = Number(11);
        return new BugRecord(
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            reader.GetString(2),
            reader.GetString(3),
            Text(4),
            Number(5) is { } reach ? (int)reach : null,
            Text(6),
            Text(7),
            Text(8),
            Text(9),
            Text(10),
            revivable is { } r ? r != 0 : null,
            Text(12),
            Text(13),
            Text(14),
            Text(15));
    }

    internal void Update(string id, BugUpdate update, string actor, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (update.Visibility != null && !BugValues.Visibilities.Contains(update.Visibility, StringComparer.Ordinal))
        {
            throw new CustosException($"visibility が正しくありません：{update.Visibility}");
        }

        if (update.Severity != null && !BugValues.Severities.Contains(update.Severity, StringComparer.Ordinal))
        {
            throw new CustosException($"severity が正しくありません：{update.Severity}");
        }

        if (update.Reach is < 0)
        {
            throw new CustosException("reach は 0 以上にしてください");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadOne(connection, transaction, id) ?? throw new CustosException($"登録簿にない ID です：{id}");
        var at = Now();

        void Apply(string field, string? oldValue, object? newValue, string sql)
        {
            if (newValue == null)
            {
                return;
            }

            var newText = newValue switch
            {
                bool b => b ? "1" : "0",
                int n => n.ToString(CultureInfo.InvariantCulture),
                string s => s,
                _ => Convert.ToString(newValue, CultureInfo.InvariantCulture),
            };
            if (string.Equals(oldValue, newText, StringComparison.Ordinal))
            {
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$value", newValue is bool flag ? (flag ? 1 : 0) : newValue);
                command.ExecuteNonQuery();
            }

            AppendChange(connection, transaction, id, at, field, oldValue, newText, actor, reason);
        }

        Apply("introduced_in", current.IntroducedIn, update.IntroducedIn, "UPDATE bugs SET introduced_in = $value WHERE id = $id");
        Apply("fixed_in", current.FixedIn, update.FixedIn, "UPDATE bugs SET fixed_in = $value WHERE id = $id");
        Apply("reach", current.Reach?.ToString(CultureInfo.InvariantCulture), update.Reach, "UPDATE bugs SET reach = $value WHERE id = $id");
        Apply("visibility", current.Visibility, update.Visibility, "UPDATE bugs SET visibility = $value WHERE id = $id");
        Apply("category", current.Category, update.Category, "UPDATE bugs SET category = $value WHERE id = $id");
        Apply("revivable", current.Revivable is { } rv ? (rv ? "1" : "0") : null, update.Revivable, "UPDATE bugs SET revivable = $value WHERE id = $id");
        Apply("confirm_ref", current.ConfirmRef, update.ConfirmRef, "UPDATE bugs SET confirm_ref = $value WHERE id = $id");
        Apply("severity", current.Severity, update.Severity, "UPDATE bugs SET severity = $value WHERE id = $id");
        transaction.Commit();
    }

    internal void AppendUsedIn(string id, string calibrationId, string actor)
    {
        if (calibrationId.Contains(',', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(calibrationId))
        {
            throw new CustosException($"校正の ID にカンマは使えません：{calibrationId}");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadOne(connection, transaction, id) ?? throw new CustosException($"登録簿にない ID です：{id}");
        var used = (current.UsedIn ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (used.Contains(calibrationId, StringComparer.Ordinal))
        {
            return;
        }

        used.Add(calibrationId);
        var newValue = string.Join(",", used);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE bugs SET used_in = $value WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$value", newValue);
            command.ExecuteNonQuery();
        }

        AppendChange(connection, transaction, id, Now(), "used_in", current.UsedIn, newValue, actor, "校正で使用");
        transaction.Commit();
    }

    private static void AppendChange(SqliteConnection connection, SqliteTransaction transaction, string id, string at, string field, string? oldValue, string? newValue, string actor, string? reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bug_changes (bug_id, at, field, old_value, new_value, actor, reason)
            VALUES ($id, $at, $field, $old, $new, $actor, $reason)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$field", field);
        command.Parameters.AddWithValue("$old", (object?)oldValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$new", (object?)newValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal void Link(string id, string kind, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        using var connection = Open();
        if (ReadOne(connection, null, id) == null)
        {
            throw new CustosException($"登録簿にない ID です：{id}");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO bug_links (bug_id, kind, reference, at) VALUES ($id, $kind, $ref, $at)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$ref", reference);
        command.Parameters.AddWithValue("$at", Now());
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<BugLink> Links(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bug_id, kind, reference, at FROM bug_links WHERE bug_id = $id ORDER BY seq";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var list = new List<BugLink>();
        while (reader.Read())
        {
            list.Add(new BugLink(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
        }

        return list;
    }

    internal IReadOnlyList<BugChange> History(string? id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (id == null)
        {
            command.CommandText = "SELECT seq, bug_id, at, field, old_value, new_value, actor, reason FROM bug_changes ORDER BY seq";
        }
        else
        {
            command.CommandText = "SELECT seq, bug_id, at, field, old_value, new_value, actor, reason FROM bug_changes WHERE bug_id = $id ORDER BY seq";
            command.Parameters.AddWithValue("$id", id);
        }

        using var reader = command.ExecuteReader();
        var list = new List<BugChange>();
        while (reader.Read())
        {
            list.Add(new BugChange(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return list;
    }
}
