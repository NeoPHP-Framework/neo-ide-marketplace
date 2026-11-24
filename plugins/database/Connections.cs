using System.IO;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

public enum Driver { MySql, Sqlite }

public sealed class ConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public Driver Driver { get; set; } = Driver.MySql;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Database { get; set; } = "";
    /// <summary>SQLite file.</summary>
    public string File { get; set; } = "";
    /// <summary>From DATABASE_URL of the project (not saved, password from the URL).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool FromEnv { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? EnvPassword { get; set; }

    public string Describe() => Driver == Driver.Sqlite ? Path.GetFileName(File) : $"{User}@{Host}:{Port}{(Database.Length > 0 ? "/" + Database : "")}";
}

/// <summary>DATABASE_URL of NeoPHP: mysql://user:pass@host:3306/db?charset=utf8mb4, sqlite:///path/to/file.db</summary>
public static class DatabaseUrl
{
    public static ConnectionInfo? Parse(string? url, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        var scheme = url.Split(':')[0].ToLowerInvariant();
        if (scheme is "sqlite" or "sqlite3" or "pdo-sqlite")
        {
            var path = url[(url.IndexOf(':') + 1)..].TrimStart('/');
            var q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            path = Uri.UnescapeDataString(path);
            if (path == ":memory:") return null;
            // sqlite:///C:/x.db → C:/x.db ; sqlite:///var/data.db (relative, rare) → project/var/data.db
            if (!(path.Length > 1 && path[1] == ':') && !Path.IsPathRooted(path)) path = Path.Combine(projectRoot, path);
            return new ConnectionInfo { Driver = Driver.Sqlite, File = Path.GetFullPath(path), Name = "Project (.env)", FromEnv = true };
        }
        if (scheme is not ("mysql" or "mariadb" or "pdo-mysql" or "mysql2")) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var user = "root";
        string? pass = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) pass = Uri.UnescapeDataString(parts[1]);
        }
        return new ConnectionInfo
        {
            Driver = Driver.MySql, Host = uri.Host.Length > 0 ? uri.Host : "127.0.0.1", Port = uri.Port > 0 ? uri.Port : 3306,
            User = user, EnvPassword = pass, Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')), Name = "Project (.env)", FromEnv = true,
        };
    }
}

/// <summary>The connections: the one of the project (.env) + those saved by the user (passwords with DPAPI).</summary>
public sealed class ConnectionStore
{
    private readonly IPluginContext _ctx;
    private List<ConnectionInfo> _saved;

    public ConnectionStore(IPluginContext ctx)
    {
        _ctx = ctx;
        _saved = ctx.Settings.Get<List<ConnectionInfo>>("connections") ?? new();
    }

    public event Action? Changed;

    public List<ConnectionInfo> All()
    {
        var list = new List<ConnectionInfo>();
        var project = _ctx.Ide.Project;
        if (project != null && DatabaseUrl.Parse(project.GetEnv("DATABASE_URL"), project.Root) is { } env) list.Add(env);
        list.AddRange(_saved);
        return list;
    }

    public string? Password(ConnectionInfo c) => c.FromEnv ? c.EnvPassword : _ctx.Settings.GetSecret("password:" + c.Id);

    public void Save(ConnectionInfo c, string? password)
    {
        _saved.RemoveAll(x => x.Id == c.Id);
        _saved.Add(c);
        _ctx.Settings.Set("connections", _saved);
        _ctx.Settings.SetSecret("password:" + c.Id, password);
        Changed?.Invoke();
    }

    public void Remove(ConnectionInfo c)
    {
        _saved.RemoveAll(x => x.Id == c.Id);
        _ctx.Settings.Set("connections", _saved);
        _ctx.Settings.SetSecret("password:" + c.Id, null);
        Changed?.Invoke();
    }

    public void RaiseChanged() => Changed?.Invoke();
}
