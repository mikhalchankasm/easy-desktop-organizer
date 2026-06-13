using System.IO;
using DesktopOrganizer.Models;
using Microsoft.Data.Sqlite;

namespace DesktopOrganizer.Data;

/// <summary>
/// Локальное SQLite-хранилище в %LOCALAPPDATA%\DesktopOrganizer\app.db (разделы 8 и 12 ТЗ).
/// Все данные хранятся только локально.
/// </summary>
public sealed class Db : IDisposable
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrganizer");

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private SqliteTransaction? _tx;

    public Db()
    {
        Directory.CreateDirectory(AppDataDir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(AppDataDir, "app.db")}");
        _conn.Open();
        Init();
    }

    private void Init()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS Boxes(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                X REAL NOT NULL DEFAULT 100,
                Y REAL NOT NULL DEFAULT 100,
                Width REAL NOT NULL DEFAULT 320,
                Height REAL NOT NULL DEFAULT 240,
                BackgroundColor TEXT NOT NULL DEFAULT '#CC1E242C',
                Opacity REAL NOT NULL DEFAULT 1.0,
                IsLocked INTEGER NOT NULL DEFAULT 0,
                IsCollapsed INTEGER NOT NULL DEFAULT 0,
                IsHidden INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT, UpdatedAt TEXT);

            CREATE TABLE IF NOT EXISTS Items(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BoxId INTEGER NOT NULL REFERENCES Boxes(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL,
                FullPath TEXT NOT NULL,
                ItemType TEXT NOT NULL DEFAULT 'File',
                Extension TEXT NOT NULL DEFAULT '',
                DisplayOrder INTEGER NOT NULL DEFAULT 0,
                IsMissing INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT, UpdatedAt TEXT);

            CREATE INDEX IF NOT EXISTS IX_Items_BoxId ON Items(BoxId);
            CREATE INDEX IF NOT EXISTS IX_Items_FullPath ON Items(FullPath);

            CREATE TABLE IF NOT EXISTS Settings(
                Key TEXT PRIMARY KEY,
                Value TEXT,
                UpdatedAt TEXT);
            """);
        Exec("PRAGMA foreign_keys = ON;");
        Exec("PRAGMA journal_mode = WAL;");   // устойчивее к сбоям
        Exec("PRAGMA busy_timeout = 5000;");  // не падать на кратковременных блокировках

        if (!ColumnExists("Items", "HiddenByApp"))
            Exec("ALTER TABLE Items ADD COLUMN HiddenByApp INTEGER NOT NULL DEFAULT 0");
        if (!ColumnExists("Boxes", "IconSize"))
            Exec("ALTER TABLE Boxes ADD COLUMN IconSize INTEGER NOT NULL DEFAULT 32");
        if (!ColumnExists("Items", "AddedAttributes"))
        {
            // Какие биты атрибутов (Hidden=2, System=4) поставило приложение — чтобы при
            // возврате снять РОВНО их и не испортить исходные атрибуты пользователя.
            // ALTER + backfill в одной транзакции: при сбое посередине откатывается целиком,
            // и миграция повторится при следующем запуске (колонка не останется без backfill).
            RunInTransaction(() =>
            {
                Exec("ALTER TABLE Items ADD COLUMN AddedAttributes INTEGER NOT NULL DEFAULT 0");
                // Legacy: старые версии всегда ставили Hidden|System (=6).
                Exec("UPDATE Items SET AddedAttributes=6 WHERE HiddenByApp=1 AND AddedAttributes=0");
            });
        }
    }

    private bool ColumnExists(string table, string column)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Now() => DateTime.UtcNow.ToString("o");

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Выполняет несколько изменений атомарно (всё или ничего).</summary>
    private void RunInTransaction(Action body)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            _tx = tx;
            try
            {
                body();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                _tx = null;
            }
        }
    }

    // ---------- Boxes ----------

    public List<Box> GetBoxes()
    {
        var list = new List<Box>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Id,Name,X,Y,Width,Height,BackgroundColor,Opacity,IsLocked,IsCollapsed,IsHidden,SortOrder,IconSize FROM Boxes ORDER BY SortOrder, Id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Box
                {
                    Id = r.GetInt64(0), Name = r.GetString(1),
                    X = r.GetDouble(2), Y = r.GetDouble(3),
                    Width = r.GetDouble(4), Height = r.GetDouble(5),
                    BackgroundColor = r.GetString(6), Opacity = r.GetDouble(7),
                    IsLocked = r.GetInt64(8) != 0, IsCollapsed = r.GetInt64(9) != 0,
                    IsHidden = r.GetInt64(10) != 0, SortOrder = (int)r.GetInt64(11),
                    IconSize = (int)r.GetInt64(12),
                });
            }
        }
        return list;
    }

    public long InsertBox(Box b)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Boxes(Name,X,Y,Width,Height,BackgroundColor,Opacity,IsLocked,IsCollapsed,IsHidden,SortOrder,CreatedAt,UpdatedAt)
                VALUES(@n,@x,@y,@w,@h,@bg,@op,@lk,@cl,@hd,@so,@t,@t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@n", b.Name);
            cmd.Parameters.AddWithValue("@x", b.X);
            cmd.Parameters.AddWithValue("@y", b.Y);
            cmd.Parameters.AddWithValue("@w", b.Width);
            cmd.Parameters.AddWithValue("@h", b.Height);
            cmd.Parameters.AddWithValue("@bg", b.BackgroundColor);
            cmd.Parameters.AddWithValue("@op", b.Opacity);
            cmd.Parameters.AddWithValue("@lk", b.IsLocked ? 1 : 0);
            cmd.Parameters.AddWithValue("@cl", b.IsCollapsed ? 1 : 0);
            cmd.Parameters.AddWithValue("@hd", b.IsHidden ? 1 : 0);
            cmd.Parameters.AddWithValue("@so", b.SortOrder);
            cmd.Parameters.AddWithValue("@t", Now());
            b.Id = (long)cmd.ExecuteScalar()!;
            return b.Id;
        }
    }

    public void UpdateBox(Box b) => Exec("""
        UPDATE Boxes SET Name=@n, X=@x, Y=@y, Width=@w, Height=@h, BackgroundColor=@bg, Opacity=@op,
            IsLocked=@lk, IsCollapsed=@cl, IsHidden=@hd, SortOrder=@so, IconSize=@ic, UpdatedAt=@t WHERE Id=@id
        """,
        ("@n", b.Name), ("@x", b.X), ("@y", b.Y), ("@w", b.Width), ("@h", b.Height),
        ("@bg", b.BackgroundColor), ("@op", b.Opacity),
        ("@lk", b.IsLocked ? 1 : 0), ("@cl", b.IsCollapsed ? 1 : 0), ("@hd", b.IsHidden ? 1 : 0),
        ("@so", b.SortOrder), ("@ic", b.IconSize), ("@t", Now()), ("@id", b.Id));

    public void DeleteBox(long boxId) => RunInTransaction(() =>
    {
        Exec("DELETE FROM Items WHERE BoxId=@id", ("@id", boxId));
        Exec("DELETE FROM Boxes WHERE Id=@id", ("@id", boxId));
    });

    // ---------- Items ----------

    public List<BoxItem> GetItems(long boxId)
    {
        var list = new List<BoxItem>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Id,BoxId,Name,FullPath,ItemType,Extension,DisplayOrder,IsMissing,HiddenByApp,AddedAttributes FROM Items WHERE BoxId=@b ORDER BY DisplayOrder, Id";
            cmd.Parameters.AddWithValue("@b", boxId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadItem(r));
        }
        return list;
    }

    private static BoxItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), BoxId = r.GetInt64(1), Name = r.GetString(2),
        FullPath = r.GetString(3), ItemType = r.GetString(4), Extension = r.GetString(5),
        DisplayOrder = (int)r.GetInt64(6), IsMissing = r.GetInt64(7) != 0,
        HiddenByApp = r.GetInt64(8) != 0,
        AddedAttributes = (System.IO.FileAttributes)r.GetInt64(9),
    };

    public long InsertItem(BoxItem it)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _tx;
            cmd.CommandText = """
                INSERT INTO Items(BoxId,Name,FullPath,ItemType,Extension,DisplayOrder,IsMissing,HiddenByApp,AddedAttributes,CreatedAt,UpdatedAt)
                VALUES(@b,@n,@p,@tp,@e,@o,@m,@hb,@aa,@t,@t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@hb", it.HiddenByApp ? 1 : 0);
            cmd.Parameters.AddWithValue("@aa", (long)it.AddedAttributes);
            cmd.Parameters.AddWithValue("@b", it.BoxId);
            cmd.Parameters.AddWithValue("@n", it.Name);
            cmd.Parameters.AddWithValue("@p", it.FullPath);
            cmd.Parameters.AddWithValue("@tp", it.ItemType);
            cmd.Parameters.AddWithValue("@e", it.Extension);
            cmd.Parameters.AddWithValue("@o", it.DisplayOrder);
            cmd.Parameters.AddWithValue("@m", it.IsMissing ? 1 : 0);
            cmd.Parameters.AddWithValue("@t", Now());
            it.Id = (long)cmd.ExecuteScalar()!;
            return it.Id;
        }
    }

    public void DeleteItem(long itemId) => Exec("DELETE FROM Items WHERE Id=@id", ("@id", itemId));

    public void RenameItem(long itemId, string name) =>
        Exec("UPDATE Items SET Name=@n, UpdatedAt=@t WHERE Id=@id", ("@n", name), ("@t", Now()), ("@id", itemId));

    public void MoveItemToBox(long itemId, long targetBoxId) =>
        Exec("UPDATE Items SET BoxId=@b, UpdatedAt=@t WHERE Id=@id", ("@b", targetBoxId), ("@t", Now()), ("@id", itemId));

    public void UpdateItemPath(long itemId, string path) =>
        Exec("UPDATE Items SET FullPath=@p, UpdatedAt=@t WHERE Id=@id", ("@p", path), ("@t", Now()), ("@id", itemId));

    public void SetItemHidden(long itemId, bool hidden, System.IO.FileAttributes added) =>
        Exec("UPDATE Items SET HiddenByApp=@h, AddedAttributes=@aa, UpdatedAt=@t WHERE Id=@id",
            ("@h", hidden ? 1 : 0), ("@aa", (long)added), ("@t", Now()), ("@id", itemId));

    public void UpdateItemOrder(long itemId, int order) =>
        Exec("UPDATE Items SET DisplayOrder=@o, UpdatedAt=@t WHERE Id=@id", ("@o", order), ("@t", Now()), ("@id", itemId));

    public void SetItemMissing(long itemId, bool missing) =>
        Exec("UPDATE Items SET IsMissing=@m WHERE Id=@id", ("@m", missing ? 1 : 0), ("@id", itemId));

    /// <summary>Элементы, скрытые приложением (для восстановления/повторного скрытия с правильными битами).</summary>
    public List<BoxItem> GetHiddenItems()
    {
        var list = new List<BoxItem>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Id,BoxId,FullPath,AddedAttributes FROM Items WHERE HiddenByApp=1";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new BoxItem
                {
                    Id = r.GetInt64(0),
                    BoxId = r.GetInt64(1),
                    FullPath = r.GetString(2),
                    AddedAttributes = (System.IO.FileAttributes)r.GetInt64(3),
                    HiddenByApp = true,
                });
        }
        return list;
    }

    /// <summary>Все пути, уже добавленные в какие-либо коробки (для автоорганизации, чтобы не дублировать).</summary>
    public HashSet<string> GetAllItemPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT FullPath FROM Items";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        return set;
    }

    // ---------- Поиск (раздел 5.7 ТЗ) ----------

    public List<SearchResult> SearchItems(string query, int limit = 50)
    {
        var list = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return list;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT i.Id, i.BoxId, i.Name, i.FullPath, i.ItemType, b.Name
                FROM Items i JOIN Boxes b ON b.Id = i.BoxId
                WHERE i.Name LIKE @q OR i.FullPath LIKE @q OR i.Extension LIKE @q OR b.Name LIKE @q
                ORDER BY i.Name LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@q", "%" + query.Trim() + "%");
            cmd.Parameters.AddWithValue("@lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new SearchResult
                {
                    ItemId = r.GetInt64(0), BoxId = r.GetInt64(1), Name = r.GetString(2),
                    FullPath = r.GetString(3), ItemType = r.GetString(4), BoxName = r.GetString(5),
                });
            }
        }
        return list;
    }

    // ---------- Settings ----------

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key=@k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value) => Exec(
        "INSERT INTO Settings(Key,Value,UpdatedAt) VALUES(@k,@v,@t) ON CONFLICT(Key) DO UPDATE SET Value=@v, UpdatedAt=@t",
        ("@k", key), ("@v", value), ("@t", Now()));

    public void Dispose() => _conn.Dispose();
}
