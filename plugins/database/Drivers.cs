using System.IO;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace NeoIDE.Plugin.Database;

public sealed record DbObject(string Name, bool IsView, long? Rows);
public sealed record DbColumn(string Name, string Type, bool Nullable, string Key, string? Default, string Extra);

public sealed class QueryResult
{
    public DataTable? Table { get; init; }
    public int Affected { get; init; } = -1;
    public TimeSpan Elapsed { get; init; }
    public bool Truncated { get; init; }
    public int Statements { get; init; } = 1;
}

/// <summary>Database access shared by MySQL and SQLite (ADO.NET). One connection per open connection node.</summary>
public abstract class DbDriver : IAsyncDisposable
{
    protected DbConnection? Connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public abstract Driver Kind { get; }
    public abstract string Quote(string identifier);
    protected abstract DbConnection Create();
    public abstract Task<List<string>> DatabasesAsync();
    public abstract Task<List<DbObject>> TablesAsync(string database);
    public abstract Task<List<DbColumn>> ColumnsAsync(string database, string table);
    public abstract Task<string> DdlAsync(string database, string table);
    /// <summary>Qualified name for the queries of the table view.</summary>
    public abstract string Qualified(string database, string table);
    public virtual Task UseAsync(string? database) => Task.CompletedTask;
    public string? ServerVersion => Connection?.ServerVersion;

    public async Task OpenAsync()
    {
        if (Connection is { State: ConnectionState.Open }) return;
        Connection?.Dispose();
        Connection = Create();
        await Connection.OpenAsync();
    }

    public async Task<QueryResult> QueryAsync(string sql, string? database, int maxRows, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await OpenAsync();
            if (database != null) await UseAsync(database);
            var sw = Stopwatch.StartNew();
            await using var cmd = Connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            DataTable? last = null;
            var truncated = false;
            var statements = 0;
            do
            {
                statements++;
                if (reader.FieldCount == 0) continue;
                var table = new DataTable();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var type = reader.GetFieldType(i);
                    if (type == typeof(byte[]) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(MySqlDateTime)) type = typeof(string);
                    // column names "c0, c1…": a DataGrid binding breaks on names with dots, brackets or slashes; the caption keeps the real name
                    table.Columns.Add(new DataColumn("c" + i, type) { Caption = reader.GetName(i) });
                }
                var values = new object[reader.FieldCount];
                var count = 0;
                while (await reader.ReadAsync(ct))
                {
                    if (count >= maxRows) { truncated = true; break; }
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object v;
                        try { v = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i); }
                        catch { v = "?"; }
                        values[i] = v switch
                        {
                            byte[] b => b.Length <= 64 ? "0x" + Convert.ToHexString(b) : $"0x{Convert.ToHexString(b, 0, 32)}… ({b.Length} bytes)",
                            DateTimeOffset or TimeSpan or Guid or MySqlDateTime => v.ToString()!,
                            _ => v,
                        };
                    }
                    table.Rows.Add(values);
                    count++;
                }
                last = table;
            } while (!truncated && await reader.NextResultAsync(ct));
            var affected = reader.RecordsAffected;
            return new QueryResult { Table = last, Affected = affected, Elapsed = sw.Elapsed, Truncated = truncated, Statements = statements };
        }
        finally { _lock.Release(); }
    }

    /// <summary>Single value (first column of the first row).</summary>
    public async Task<object?> ScalarAsync(string sql, string? database = null)
    {
        var r = await QueryAsync(sql, database, 1);
        return r.Table is { Rows.Count: > 0 } t ? t.Rows[0][0] : null;
    }

    protected async Task<DataTable> TableAsync(string sql, string? database = null) =>
        (await QueryAsync(sql, database, 100_000)).Table ?? new DataTable();

    protected static string S(object? v) => v is null or DBNull ? "" : v.ToString() ?? "";

    public static DbDriver Create(ConnectionInfo c, string? password) => c.Driver switch
    {
        Driver.Sqlite => new SqliteDriver(c.File),
        _ => new MySqlDriver(c, password),
    };

    public async ValueTask DisposeAsync()
    {
        if (Connection != null) await Connection.DisposeAsync();
        Connection = null;
    }
}

public sealed class MySqlDriver : DbDriver
{
    private readonly ConnectionInfo _info;
    private readonly string? _password;
    private string? _current;

    public MySqlDriver(ConnectionInfo info, string? password) { _info = info; _password = password; }

    public override Driver Kind => Driver.MySql;
    public override string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";
    public override string Qualified(string database, string table) => $"{Quote(database)}.{Quote(table)}";

    protected override DbConnection Create() => new MySqlConnection(new MySqlConnectionStringBuilder
    {
        Server = _info.Host, Port = (uint)_info.Port, UserID = _info.User, Password = _password ?? "",
        Database = _info.Database, AllowUserVariables = true, ConnectionTimeout = 8, AllowPublicKeyRetrieval = true,
        SslMode = MySqlSslMode.Preferred, CharacterSet = "utf8mb4", ConvertZeroDateTime = true,
    }.ConnectionString);

    public override async Task UseAsync(string? database)
    {
        if (string.IsNullOrEmpty(database) || database == _current) return;
        await Connection!.ChangeDatabaseAsync(database);
        _current = database;
    }

    public override async Task<List<string>> DatabasesAsync()
    {
        var t = await TableAsync("SHOW DATABASES");
        return t.Rows.Cast<DataRow>().Select(r => S(r[0])).ToList();
    }

    public override async Task<List<DbObject>> TablesAsync(string database)
    {
        var t = await TableAsync($"SELECT TABLE_NAME, TABLE_TYPE, TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{database.Replace("'", "''")}' ORDER BY TABLE_NAME");
        return t.Rows.Cast<DataRow>().Select(r => new DbObject(S(r[0]), S(r[1]).Contains("VIEW"), r[2] is DBNull ? null : Convert.ToInt64(r[2]))).ToList();
    }

    public override async Task<List<DbColumn>> ColumnsAsync(string database, string table)
    {
        var t = await TableAsync($"SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA FROM information_schema.COLUMNS " +
                                 $"WHERE TABLE_SCHEMA = '{database.Replace("'", "''")}' AND TABLE_NAME = '{table.Replace("'", "''")}' ORDER BY ORDINAL_POSITION");
        return t.Rows.Cast<DataRow>().Select(r => new DbColumn(S(r[0]), S(r[1]), S(r[2]) == "YES", S(r[3]), r[4] is DBNull ? null : S(r[4]), S(r[5]))).ToList();
    }

    public override async Task<string> DdlAsync(string database, string table)
    {
        var t = await TableAsync($"SHOW CREATE TABLE {Qualified(database, table)}");
        return t.Rows.Count > 0 ? S(t.Rows[0][1]) + ";" : "";
    }
}

public sealed class SqliteDriver : DbDriver
{
    private readonly string _file;
    public SqliteDriver(string file) => _file = file;

    public override Driver Kind => Driver.Sqlite;
    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    public override string Qualified(string database, string table) => Quote(table);

    protected override DbConnection Create()
    {
        if (!File.Exists(_file)) throw new FileNotFoundException($"SQLite database not found: {_file} (run php bin/neo database:create or a migration first).");
        return new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _file, Mode = SqliteOpenMode.ReadWrite }.ConnectionString);
    }

    public override Task<List<string>> DatabasesAsync() => Task.FromResult(new List<string> { "main" });

    public override async Task<List<DbObject>> TablesAsync(string database)
    {
        var t = await TableAsync("SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name");
        return t.Rows.Cast<DataRow>().Select(r => new DbObject(S(r[0]), S(r[1]) == "view", null)).ToList();
    }

    public override async Task<List<DbColumn>> ColumnsAsync(string database, string table)
    {
        var t = await TableAsync($"PRAGMA table_info({Quote(table)})");
        return t.Rows.Cast<DataRow>().Select(r => new DbColumn(S(r[1]), S(r[2]), S(r[3]) == "0", S(r[5]) != "0" ? "PRI" : "", r[4] is DBNull ? null : S(r[4]), "")).ToList();
    }

    public override async Task<string> DdlAsync(string database, string table)
    {
        var sql = await ScalarAsync($"SELECT sql FROM sqlite_master WHERE name = '{table.Replace("'", "''")}'");
        return S(sql) + ";";
    }
}
