using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.OpenApi;

public sealed record Endpoint(string Method, string Path, string Summary, string Tag, string? OperationId, JsonObject Operation);

public sealed class OpenApiPlugin : IPlugin
{
    internal static IPluginContext Ctx = null!;

    public void Activate(IPluginContext context)
    {
        Ctx = context;
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "openapi", Title = "OpenAPI", Icon = "", Location = ToolWindowLocation.Left,
            Create = () => new OpenApiView(context),
        });
    }

    public static readonly string[] Methods = { "get", "post", "put", "patch", "delete", "head", "options" };

    public static List<Endpoint> Endpoints(JsonObject doc)
    {
        var list = new List<Endpoint>();
        if (doc["paths"] is not JsonObject paths) return list;
        foreach (var (path, node) in paths)
        {
            if (node is not JsonObject item) continue;
            foreach (var m in Methods)
            {
                if (item[m] is not JsonObject op) continue;
                var tag = op["tags"] is JsonArray { Count: > 0 } tags ? tags[0]?.GetValue<string>() ?? "default" : "default";
                var summary = op["summary"]?.GetValue<string>() ?? op["description"]?.GetValue<string>() ?? "";
                // parameters declared on the path apply to every operation
                if (item["parameters"] is JsonArray shared && op["parameters"] == null) op["parameters"] = shared.DeepClone();
                list.Add(new Endpoint(m.ToUpperInvariant(), path, summary, tag, op["operationId"]?.GetValue<string>(), op));
            }
        }
        return list;
    }

    public static string MethodColor(string m) => m switch
    {
        "GET" or "HEAD" => "#5FB865", "POST" => "#4C9BE8", "PUT" or "PATCH" => "#E8A33D", "DELETE" => "#E55B5B", _ => "#868A91",
    };

    /// <summary>Resolves "#/components/schemas/Book" references (one level, enough to read the document).</summary>
    public static JsonNode? Resolve(JsonObject doc, JsonNode? node, int depth = 0)
    {
        if (node is not JsonObject o || depth > 6) return node?.DeepClone();
        if (o["$ref"]?.GetValue<string>() is { } r && r.StartsWith("#/"))
        {
            JsonNode? target = doc;
            foreach (var part in r[2..].Split('/')) target = target?[part];
            return Resolve(doc, target, depth + 1);
        }
        var copy = new JsonObject();
        foreach (var (k, v) in o) copy[k] = v is JsonObject or JsonArray ? (v is JsonArray a ? new JsonArray(a.Select(x => Resolve(doc, x, depth + 1)).ToArray()) : Resolve(doc, v, depth + 1)) : v?.DeepClone();
        return copy;
    }
}

public sealed class OpenApiView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly TextBox _search = Ui.TextBox(tooltip: "Filter by path, method, tag or summary");
    private readonly TreeView _tree = new() { Padding = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _status = Ui.Text("", secondary: true, size: 11.5);
    private JsonObject? _doc;
    private List<Endpoint> _endpoints = new();
    private bool _loaded;

    public OpenApiView(IPluginContext ctx)
    {
        _ctx = ctx;
        var bar = Ui.Toolbar(
            Ui.ToolButton("", "Reload (openapi:dump)", async () => await Load()),
            Ui.ToolButton("", "Open the whole document", OpenDocument),
            Ui.ToolButton("", "Export to public/openapi.json", async () => await Export("json")),
            Ui.ToolButton("", "Export to public/openapi.yaml", async () => await Export("yaml")),
            Ui.ToolButton("", "Open config/framework/api.yaml", () => { if (ctx.Ide.Project is { } p) ctx.Ide.OpenFile(Path.Combine(p.Root, "config", "framework", "api.yaml")); }));
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var searchBox = new Border { Padding = new Thickness(8, 4, 6, 2), Child = _search };
        SetDock(searchBox, Dock.Top);
        Children.Add(searchBox);
        var statusBox = new Border { Padding = new Thickness(10, 2, 10, 2), Child = _status };
        _status.TextWrapping = TextWrapping.Wrap;
        SetDock(statusBox, Dock.Top);
        Children.Add(statusBox);
        Children.Add(_tree);
        _search.TextChanged += (_, _) => Fill();
        _tree.MouseDoubleClick += (_, e) => { if (OpenSelected()) e.Handled = true; };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenSelected(); };
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { if (_loaded) await Load(); });
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await Load(); } };
    }

    private async Task Load()
    {
        if (_ctx.Ide.Project == null) { _status.Text = "No project"; return; }
        _status.Text = "Generating the document…";
        var r = await _ctx.Ide.RunNeoAsync("openapi:dump --format=json");
        var json = r.Output.Trim();
        var start = json.IndexOf('{');
        try
        {
            _doc = JsonNode.Parse(start >= 0 ? json[start..] : json) as JsonObject ?? throw new JsonException("empty document");
        }
        catch (Exception ex)
        {
            _doc = null;
            _endpoints = new();
            _status.Text = "openapi:dump failed: " + (r.Error.Trim().Length > 0 ? r.Error.Trim() : ex.Message);
            Fill();
            return;
        }
        _endpoints = OpenApiPlugin.Endpoints(_doc);
        Fill();
    }

    private void Fill()
    {
        _tree.Items.Clear();
        if (_doc == null) return;
        var q = _search.Text.Trim();
        var shown = _endpoints.Where(e => q.Length == 0 || e.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Method.Equals(q, StringComparison.OrdinalIgnoreCase)
                                          || e.Tag.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in shown.GroupBy(e => e.Tag).OrderBy(g => g.Key))
        {
            var header = Ui.Text($"{group.Key}  ", size: 12.5);
            header.FontWeight = FontWeights.SemiBold;
            var node = new TreeViewItem { Header = header, IsExpanded = true };
            foreach (var e in group)
            {
                var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                top.Children.Add(Ui.Badge(e.Method, Ui.Hex(OpenApiPlugin.MethodColor(e.Method)), 52));
                var path = Ui.Text(e.Path);
                path.FontFamily = Ui.Mono;
                top.Children.Add(path);
                row.Children.Add(top);
                if (e.Summary.Length > 0) row.Children.Add(Ui.Text(e.Summary, secondary: true, size: 11.5));
                node.Items.Add(new TreeViewItem { Header = row, Tag = e, ToolTip = e.OperationId });
            }
            _tree.Items.Add(node);
        }
        var title = _doc["info"]?["title"]?.GetValue<string>() ?? "API";
        var version = _doc["info"]?["version"]?.GetValue<string>() ?? "";
        _status.Text = _endpoints.Count == 0
            ? $"{title} {version}: no documented route. Only the routes matching openapi.paths of config/framework/api.yaml (default ^/api) are documented."
            : $"{title} {version} · {shown.Count} of {_endpoints.Count} endpoint(s) · OpenAPI {_doc["openapi"]?.GetValue<string>()}";
    }

    private bool OpenSelected()
    {
        if (_tree.SelectedItem is not TreeViewItem { Tag: Endpoint e } || _doc == null) return false;
        var doc = _doc;
        _ctx.Ide.OpenDocument($"openapi:{e.Method}:{e.Path}", $"{e.Method} {e.Path}", () => new EndpointView(_ctx, doc, e), "");
        return true;
    }

    private void OpenDocument()
    {
        if (_doc == null) return;
        var text = _doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _ctx.Ide.OpenDocument("openapi:document", "openapi.json", () =>
        {
            var ed = _ctx.Ide.CreateCodeEditor("json", text);
            return ed.Element;
        }, "");
    }

    private async Task Export(string format)
    {
        if (_ctx.Ide.Project is not { } p) return;
        var target = $"public/openapi.{format}";
        var r = await _ctx.Ide.RunNeoAsync($"openapi:dump --format={format} --output={target}");
        if (r.Success)
        {
            _ctx.Ide.Notify($"{target} written", NotificationKind.Success);
            _ctx.Ide.OpenFile(Path.Combine(p.Root, target.Replace('/', Path.DirectorySeparatorChar)));
        }
        else _ctx.Ide.Notify("openapi:dump failed: " + (r.Error + r.Output).Trim(), NotificationKind.Error);
    }
}

/// <summary>Editor tab of one operation: description, parameters, body, responses (schemas resolved), curl.</summary>
public sealed class EndpointView : DockPanel
{
    public EndpointView(IPluginContext ctx, JsonObject doc, Endpoint e)
    {
        this.Themed(BackgroundProperty, "EditorBg");
        var baseUrl = (ctx.Ide.Project?.GetEnv("APP_URL") is { Length: > 0 } u ? u : "http://127.0.0.1:8000").TrimEnd('/');
        var head = new StackPanel { Margin = new Thickness(16, 12, 16, 10) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Ui.Badge(e.Method, Ui.Hex(OpenApiPlugin.MethodColor(e.Method)), 56));
        var path = Ui.Text(e.Path, size: 15);
        path.FontFamily = Ui.Mono;
        line.Children.Add(path);
        head.Children.Add(line);
        if (e.Summary.Length > 0)
        {
            var s = Ui.Text(e.Summary, secondary: true);
            s.TextWrapping = TextWrapping.Wrap;
            s.Margin = new Thickness(0, 6, 0, 0);
            head.Children.Add(s);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Ui.Button("Copy as curl", () => { Clipboard.SetText(Curl(doc, e, baseUrl)); ctx.Ide.Notify("curl command copied", NotificationKind.Success); }));
        buttons.Children.Add(Ui.Button("Copy URL", () => Clipboard.SetText(baseUrl + e.Path)));
        head.Children.Add(buttons);
        var headBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = head }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        SetDock(headBorder, Dock.Top);
        Children.Add(headBorder);

        var resolved = OpenApiPlugin.Resolve(doc, e.Operation) as JsonObject ?? new JsonObject();
        var view = new JsonObject();
        foreach (var key in new[] { "operationId", "description", "parameters", "requestBody", "responses", "security", "deprecated" })
            if (resolved[key] is { } v) view[key] = v.DeepClone();
        var text = view.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Children.Add(ctx.Ide.CreateCodeEditor("json", text).Element);
    }

    private static string Curl(JsonObject doc, Endpoint e, string baseUrl)
    {
        var sb = new StringBuilder($"curl -X {e.Method} \"{baseUrl}{e.Path}\" -H \"Accept: application/json\"");
        var body = OpenApiPlugin.Resolve(doc, e.Operation["requestBody"]) as JsonObject;
        if (body?["content"]?["application/json"]?["schema"] is JsonObject schema)
        {
            var example = Example(schema, 0);
            sb.Append(" -H \"Content-Type: application/json\" -d '").Append(example?.ToJsonString() ?? "{}").Append('\'');
        }
        return sb.ToString();
    }

    /// <summary>Example value built from a schema (example, default, enum or a placeholder of the type).</summary>
    private static JsonNode? Example(JsonObject schema, int depth)
    {
        if (depth > 5) return null;
        if (schema["example"] is { } ex) return ex.DeepClone();
        if (schema["default"] is { } def) return def.DeepClone();
        if (schema["enum"] is JsonArray { Count: > 0 } en) return en[0]?.DeepClone();
        var type = schema["type"] is JsonArray ta ? ta.FirstOrDefault(t => t?.GetValue<string>() != "null")?.GetValue<string>() : schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object" or null when schema["properties"] is JsonObject props:
                var o = new JsonObject();
                foreach (var (k, v) in props) if (v is JsonObject ps) o[k] = Example(ps, depth + 1);
                return o;
            case "array":
                return new JsonArray(schema["items"] is JsonObject items ? Example(items, depth + 1) : null);
            case "integer": return 0;
            case "number": return 0.0;
            case "boolean": return false;
            case "string": return schema["format"]?.GetValue<string>() switch { "date-time" => "2026-01-01T00:00:00Z", "date" => "2026-01-01", "email" => "user@example.com", "uuid" => "00000000-0000-0000-0000-000000000000", _ => "string" };
            default: return null;
        }
    }
}
