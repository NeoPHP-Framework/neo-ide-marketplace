using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoIDE.Plugin.Mercure;

public sealed record MercureEvent(DateTime Received, string? Id, string? Type, string Data);

/// <summary>Configuration of the project: .env + config/packages/mercure.yaml.</summary>
public sealed class MercureConfig
{
    public string HubUrl { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Algorithm { get; set; } = "HS256";

    public static MercureConfig FromProject(NeoIDE.Sdk.IProject? project)
    {
        var c = new MercureConfig();
        if (project == null) return c;
        c.HubUrl = project.GetEnv("MERCURE_PUBLIC_URL") is { Length: > 0 } pub ? pub : project.GetEnv("MERCURE_URL") ?? "";
        c.Secret = project.GetEnv("MERCURE_JWT_SECRET") ?? "";
        var yaml = Path.Combine(project.Root, "config", "packages", "mercure.yaml");
        if (File.Exists(yaml) && Regex.Match(File.ReadAllText(yaml), @"^\s*algorithm:\s*['""]?(HS(256|384|512))", RegexOptions.Multiline) is { Success: true } m)
            c.Algorithm = m.Groups[1].Value;
        return c;
    }
}

public static class Jwt
{
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>JWT for the hub: {"mercure":{"publish":[...],"subscribe":[...]}} signed with the shared secret.</summary>
    public static string Create(string secret, string algorithm, string[]? publish, string[]? subscribe, TimeSpan lifetime)
    {
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = algorithm, ["typ"] = "JWT" }));
        var claims = new Dictionary<string, object>();
        if (publish != null) claims["publish"] = publish;
        if (subscribe != null) claims["subscribe"] = subscribe;
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["mercure"] = claims,
            ["exp"] = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds(),
        }));
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.ASCII.GetBytes(header + "." + payload);
        var sig = algorithm switch
        {
            "HS384" => HMACSHA384.HashData(key, data),
            "HS512" => HMACSHA512.HashData(key, data),
            _ => HMACSHA256.HashData(key, data),
        };
        return header + "." + payload + "." + B64(sig);
    }
}

/// <summary>Subscription (Server-Sent Events) and publication on a Mercure hub.</summary>
public sealed class MercureClient
{
    private static HttpClient CreateHttp(string url, bool streaming)
    {
        var handler = new HttpClientHandler();
        // FrankenPHP / Caddy serve https://localhost with a local CA: accepted for local hosts only
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".localhost") || u.Host.EndsWith(".test")))
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { Timeout = streaming ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(10) };
    }

    /// <summary>Hub answers? (GET without topic → 400 "Missing topic" = alive.)</summary>
    public static async Task<(bool ok, string message)> PingAsync(string hub)
    {
        if (string.IsNullOrWhiteSpace(hub)) return (false, "MERCURE_URL is empty");
        try
        {
            using var http = CreateHttp(hub, false);
            using var r = await http.GetAsync(hub);
            return r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.OK or HttpStatusCode.Unauthorized
                ? (true, $"hub reachable (HTTP {(int)r.StatusCode})")
                : (false, $"HTTP {(int)r.StatusCode} {r.ReasonPhrase}");
        }
        catch (Exception ex) { return (false, ex.InnerException?.Message ?? ex.Message); }
    }

    public static async Task<string> PublishAsync(MercureConfig config, string[] topics, string data, bool isPrivate, string? type, string? id)
    {
        using var http = CreateHttp(config.HubUrl, false);
        var jwt = Jwt.Create(config.Secret, config.Algorithm, new[] { "*" }, null, TimeSpan.FromMinutes(5));
        var form = new List<KeyValuePair<string, string>>();
        foreach (var t in topics) form.Add(new("topic", t));
        form.Add(new("data", data));
        if (isPrivate) form.Add(new("private", "on"));
        if (!string.IsNullOrWhiteSpace(type)) form.Add(new("type", type));
        if (!string.IsNullOrWhiteSpace(id)) form.Add(new("id", id));
        using var req = new HttpRequestMessage(HttpMethod.Post, config.HubUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var r = await http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)r.StatusCode}: {body.Trim()}");
        return body.Trim();
    }

    /// <summary>Reads the event stream until <paramref name="ct"/> is cancelled or the hub closes the connection.</summary>
    public static async Task SubscribeAsync(MercureConfig config, string[] topics, Action<MercureEvent> onEvent, Action onOpen, CancellationToken ct)
    {
        using var http = CreateHttp(config.HubUrl, true);
        var url = config.HubUrl + (config.HubUrl.Contains('?') ? "&" : "?") + string.Join("&", topics.Select(t => "topic=" + Uri.EscapeDataString(t)));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (config.Secret.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt.Create(config.Secret, config.Algorithm, null, topics, TimeSpan.FromHours(12)));
        using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)r.StatusCode}: {(await r.Content.ReadAsStringAsync(ct)).Trim()}");
        onOpen();
        await using var stream = await r.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null, type = null;
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0) onEvent(new MercureEvent(DateTime.Now, id, type, data.ToString().TrimEnd('\n')));
                id = type = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue; // heartbeat
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "data": data.Append(value).Append('\n'); break;
                case "id": id = value; break;
                case "event": type = value; break;
            }
        }
    }
}
