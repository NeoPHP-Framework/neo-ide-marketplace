using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.HttpClient;

public sealed class SavedRequest
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "/";
    public string Headers { get; set; } = "Accept: application/json";
    public string Body { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public int Status { get; set; }
}

public sealed class HttpClientPlugin : IPlugin
{
    internal static IPluginContext Ctx = null!;
    internal static event Action? HistoryChanged;
    private static int _counter;

    public void Activate(IPluginContext context)
    {
        Ctx = context;
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "http-client", Title = "HTTP Client", Icon = "", Location = ToolWindowLocation.Left,
            Create = () => new HistoryView(context),
        });
        context.Ide.AddMenuItem("New HTTP request", () => Open(null), "");
    }

    public static void Open(SavedRequest? request)
    {
        var n = ++_counter;
        var title = request == null ? "HTTP request" : $"{request.Method} {Shorten(request.Url)}";
        Ctx.Ide.OpenDocument($"http:{n}", title, () => new RequestView(Ctx, request ?? new SavedRequest()), "");
    }

    private static string Shorten(string url) => url.Length > 28 ? "…" + url[^27..] : url;

    public static List<SavedRequest> History() => Ctx.Settings.Get<List<SavedRequest>>("history") ?? new();

    public static void AddToHistory(SavedRequest r)
    {
        var list = History();
        list.RemoveAll(x => x.Method == r.Method && x.Url == r.Url && x.Body == r.Body && x.Headers == r.Headers);
        list.Insert(0, r);
        if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
        Ctx.Settings.Set("history", list);
        HistoryChanged?.Invoke();
    }

    public static void ClearHistory()
    {
        Ctx.Settings.Set("history", new List<SavedRequest>());
        HistoryChanged?.Invoke();
    }

    /// <summary>"/api/books" → APP_URL + path; full URLs are kept.</summary>
    public static string Resolve(string url, IProject? project)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        var baseUrl = (project?.GetEnv("APP_URL") is { Length: > 0 } u ? u : "http://127.0.0.1:8000").TrimEnd('/');
        return baseUrl + "/" + url.TrimStart('/');
    }
}

/// <summary>Left tool window: history of the requests (double-click reopens one).</summary>
public sealed class HistoryView : DockPanel
{
    private readonly ListBox _list = new();

    public HistoryView(IPluginContext ctx)
    {
        var bar = Ui.Toolbar(
            Ui.ToolButton("", "New request", () => HttpClientPlugin.Open(null)),
            Ui.ToolButton("", "Clear the history", () => { if (ctx.Ide.Confirm("HTTP history", "Delete the history of the requests?", "Delete")) HttpClientPlugin.ClearHistory(); }));
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_list);
        _list.MouseDoubleClick += (_, _) => Reopen();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Reopen(); };
        HttpClientPlugin.HistoryChanged += () => Dispatcher.BeginInvoke(Fill);
        Fill();
    }

    private void Reopen()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is SavedRequest r) HttpClientPlugin.Open(r);
    }

    private void Fill()
    {
        _list.Items.Clear();
        var history = HttpClientPlugin.History();
        foreach (var r in history)
        {
            var stack = new StackPanel { Margin = new Thickness(2) };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(Ui.Badge(r.Method, Ui.Hex(RequestView.MethodColor(r.Method)), 46));
            var url = Ui.Text(r.Url);
            url.FontFamily = Ui.Mono;
            top.Children.Add(url);
            stack.Children.Add(top);
            stack.Children.Add(Ui.Text($"{(r.Status > 0 ? r.Status.ToString() : "error")} · {r.At:dd/MM HH:mm}", secondary: true, size: 11.5));
            _list.Items.Add(new ListBoxItem { Content = stack, Tag = r, ToolTip = r.Url });
        }
        if (history.Count == 0) _list.Items.Add(new ListBoxItem { Content = Ui.Empty("No request yet.\nTools › New HTTP request, or +"), IsEnabled = false });
    }
}

/// <summary>Editor tab: request on top, response below.</summary>
public sealed class RequestView : Grid
{
    private readonly IPluginContext _ctx;
    private readonly ComboBox _method = new() { Width = 96, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _url = Ui.TextBox(tooltip: "URL, absolute or relative to APP_URL (e.g. /api/books)");
    private readonly ICodeEditor _headers, _body, _response;
    private readonly TextBlock _status = Ui.Text("Ctrl+Enter to send", secondary: true);
    private readonly TextBox _responseHeaders = new() { IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 12, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };
    private readonly Button _send;
    private string _lastBody = "";

    public static string MethodColor(string m) => m.ToUpperInvariant() switch
    {
        "GET" or "HEAD" => "#5FB865", "POST" => "#4C9BE8", "PUT" or "PATCH" => "#E8A33D", "DELETE" => "#E55B5B", _ => "#868A91",
    };

    public RequestView(IPluginContext ctx, SavedRequest r)
    {
        _ctx = ctx;
        this.Themed(BackgroundProperty, "EditorBg");
        foreach (var m in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }) _method.Items.Add(m);
        _method.SelectedItem = r.Method;
        _url.Text = r.Url;
        _headers = ctx.Ide.CreateCodeEditor("yaml", r.Headers);
        _body = ctx.Ide.CreateCodeEditor("json", r.Body);
        _response = ctx.Ide.CreateCodeEditor("json", "");
        _send = Ui.Button("Send", async () => await Send(), primary: true);
        _send.ToolTip = "Ctrl+Enter";

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });

        // request line
        var line = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        right.Children.Add(_send);
        right.Children.Add(Ui.ToolButton("", "Open in the browser (GET)", () =>
            Process.Start(new ProcessStartInfo(HttpClientPlugin.Resolve(_url.Text, _ctx.Ide.Project)) { UseShellExecute = true })));
        DockPanel.SetDock(right, Dock.Right);
        DockPanel.SetDock(_method, Dock.Left);
        line.Children.Add(right);
        line.Children.Add(_method);
        line.Children.Add(_url);
        var lineBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = line }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        Children.Add(lineBorder);

        // request headers / body
        var reqTabs = new TabControl { Style = Ui.Style("ToolTabControl"), BorderThickness = new Thickness(0) };
        reqTabs.Items.Add(new TabItem { Header = "Body", Content = _body.Element, Style = Ui.Style("ToolTabItem") });
        reqTabs.Items.Add(new TabItem { Header = "Headers", Content = _headers.Element, Style = Ui.Style("ToolTabItem") });
        SetRow(reqTabs, 1);
        Children.Add(reqTabs);

        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        splitter.Themed(BackgroundProperty, "SubtleBorder");
        SetRow(splitter, 2);
        Children.Add(splitter);

        // response
        var resp = new DockPanel();
        var statusBar = new DockPanel { Margin = new Thickness(10, 5, 10, 5) };
        var copy = Ui.ToolButton("", "Copy the response body", () => Clipboard.SetText(_lastBody));
        DockPanel.SetDock(copy, Dock.Right);
        statusBar.Children.Add(copy);
        statusBar.Children.Add(_status);
        var statusBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = statusBar }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        DockPanel.SetDock(statusBorder, Dock.Top);
        resp.Children.Add(statusBorder);
        var respTabs = new TabControl { Style = Ui.Style("ToolTabControl"), BorderThickness = new Thickness(0) };
        respTabs.Items.Add(new TabItem { Header = "Response", Content = _response.Element, Style = Ui.Style("ToolTabItem") });
        respTabs.Items.Add(new TabItem { Header = "Headers", Content = _responseHeaders, Style = Ui.Style("ToolTabItem") });
        resp.Children.Add(respTabs);
        SetRow(resp, 3);
        Children.Add(resp);

        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; await Send(); }
        };
        _url.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Send(); };
    }

    private static System.Net.Http.HttpClient CreateClient(string url)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.EndsWith(".localhost") || u.Host.EndsWith(".test")))
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private async Task Send()
    {
        var method = (string)_method.SelectedItem;
        var url = HttpClientPlugin.Resolve(_url.Text, _ctx.Ide.Project);
        var saved = new SavedRequest { Method = method, Url = _url.Text.Trim(), Headers = _headers.Text, Body = _body.Text };
        _send.IsEnabled = false;
        _status.Text = $"{method} {url} …";
        _status.Themed(TextBlock.ForegroundProperty, "FgSecondary");
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = CreateClient(url);
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            string? contentType = null;
            foreach (var raw in _headers.Text.Replace("\r", "").Split('\n'))
            {
                var colon = raw.IndexOf(':');
                if (raw.TrimStart().StartsWith('#') || colon <= 0) continue;
                var name = raw[..colon].Trim();
                var value = raw[(colon + 1)..].Trim();
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = value; continue; }
                req.Headers.TryAddWithoutValidation(name, value);
            }
            if (method is not ("GET" or "HEAD") && _body.Text.Trim().Length > 0)
            {
                contentType ??= _body.Text.TrimStart().StartsWith('{') || _body.Text.TrimStart().StartsWith('[') ? "application/json" : "text/plain";
                req.Content = new StringContent(_body.Text, Encoding.UTF8);
                req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                req.Content.Headers.ContentType!.CharSet ??= "utf-8";
            }
            using var res = await http.SendAsync(req);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            sw.Stop();
            var text = Encoding.UTF8.GetString(bytes);
            _lastBody = text;
            var ct = res.Content.Headers.ContentType?.MediaType ?? "";
            _response.Text = Pretty(text, ct);
            var headers = new StringBuilder($"HTTP/{res.Version} {(int)res.StatusCode} {res.ReasonPhrase}\n");
            foreach (var h in res.Headers.Concat(res.Content.Headers)) headers.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
            _responseHeaders.Text = headers.ToString();
            var code = (int)res.StatusCode;
            _status.Text = $"{code} {res.ReasonPhrase}  ·  {sw.ElapsedMilliseconds} ms  ·  {Size(bytes.Length)}  ·  {ct}";
            _status.Themed(TextBlock.ForegroundProperty, code >= 500 ? "ErrorFg" : code >= 400 ? "WarnFg" : "SuccessFg");
            saved.Status = code;
        }
        catch (Exception ex)
        {
            _status.Text = $"Failed after {sw.ElapsedMilliseconds} ms: {ex.InnerException?.Message ?? ex.Message}" +
                           (url.Contains("127.0.0.1") || url.Contains("localhost") ? "  (is the server started? NeoPHP › Start server)" : "");
            _status.Themed(TextBlock.ForegroundProperty, "ErrorFg");
        }
        finally { _send.IsEnabled = true; }
        HttpClientPlugin.AddToHistory(saved);
    }

    private static string Size(int b) => b < 1024 ? $"{b} B" : $"{b / 1024.0:0.0} KB";

    private static string Pretty(string text, string contentType)
    {
        if (contentType.Contains("json") || text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            try
            {
                using var doc = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch (JsonException) { }
        return text;
    }
}
