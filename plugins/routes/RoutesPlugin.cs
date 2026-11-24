using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Routes;

public sealed record Route(string Name, string[] Methods, string Path, string Controller)
{
    public string? Class => Controller.Contains("::") ? Controller[..Controller.IndexOf("::", StringComparison.Ordinal)] : Controller.Length > 0 ? Controller : null;
    public string? Method => Controller.Contains("::") ? Controller[(Controller.IndexOf("::", StringComparison.Ordinal) + 2)..] : "__invoke";
    public bool HasParameters => Path.Contains('{');
}

public sealed class RoutesPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "routes", Title = "Routes", Icon = "", Location = ToolWindowLocation.Left,
            Create = () => new RoutesView(context),
        });
    }

    /// <summary>route:list → routes. Columns: Name | Method | Path | Controller.</summary>
    public static List<Route> Parse(string output) => ConsoleTable.Parse(output)
        .Select(r => new Route(
            r.GetValueOrDefault("Name") ?? "",
            (r.GetValueOrDefault("Method") ?? r.GetValueOrDefault("Methods") ?? "").Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
            r.GetValueOrDefault("Path") ?? "",
            r.GetValueOrDefault("Controller") ?? r.GetValueOrDefault("Action") ?? ""))
        .Where(r => r.Path.Length > 0)
        .ToList();
}

public sealed class RoutesView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly TextBox _search = Ui.TextBox(tooltip: "Filter by path, name, method or controller");
    private readonly ListBox _list = new() { Padding = new Thickness(0, 2, 0, 6) };
    private readonly TextBlock _status = Ui.Text("", secondary: true, size: 11.5);
    private List<Route> _routes = new();

    public RoutesView(IPluginContext ctx)
    {
        _ctx = ctx;
        var bar = new DockPanel { Margin = new Thickness(8, 4, 6, 4) };
        var refresh = Ui.ToolButton("", "Reload the routes (route:list)", async () => await Load());
        SetDock(refresh, Dock.Right);
        bar.Children.Add(refresh);
        bar.Children.Add(_search);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var statusBox = new Border { Padding = new Thickness(10, 0, 10, 4), Child = _status };
        SetDock(statusBox, Dock.Top);
        Children.Add(statusBox);
        Children.Add(_list);
        _search.TextChanged += (_, _) => Fill();
        _list.MouseDoubleClick += (_, _) => GoTo(null);
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) GoTo(null); };
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, e) => { if (Selected == null) e.Handled = true; else _list.ContextMenu = Menu(null); };
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => await Load());
        Loaded += async (_, _) => { if (_routes.Count == 0) await Load(); };
    }

    private async Task Load()
    {
        if (_ctx.Ide.Project == null) { _status.Text = "No project"; return; }
        _status.Text = "Loading routes…";
        var r = await _ctx.Ide.RunNeoAsync("route:list");
        if (!r.Success) { _status.Text = "route:list failed: " + (r.Error.Trim().Length > 0 ? r.Error.Trim() : r.Output.Trim()); return; }
        _routes = RoutesPlugin.Parse(r.Output);
        Fill();
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        var shown = _routes.Where(r => q.Length == 0
            || r.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Controller.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Methods.Any(m => m.Equals(q, StringComparison.OrdinalIgnoreCase))).ToList();
        _list.Items.Clear();
        foreach (var r in shown) _list.Items.Add(Row(r));
        _status.Text = q.Length == 0 ? $"{_routes.Count} route(s)" : $"{shown.Count} of {_routes.Count} route(s)";
    }

    private static readonly Dictionary<string, string> MethodColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GET"] = "#5FB865", ["HEAD"] = "#5FB865", ["POST"] = "#4C9BE8", ["PUT"] = "#E8A33D", ["PATCH"] = "#E8A33D", ["DELETE"] = "#E55B5B", ["OPTIONS"] = "#868A91",
    };

    private ListBoxItem Row(Route r)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var m in r.Methods.DefaultIfEmpty("ANY"))
            top.Children.Add(Ui.Badge(m.ToUpperInvariant(), Ui.Hex(MethodColors.GetValueOrDefault(m, "#868A91")), 34));
        var path = Ui.Text(r.Path);
        path.FontFamily = Ui.Mono;
        top.Children.Add(path);
        stack.Children.Add(top);
        var sub = Ui.Text((r.Name.Length > 0 ? r.Name + "  ·  " : "") + ShortController(r.Controller), secondary: true, size: 11.5);
        sub.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(sub);
        return new ListBoxItem { Content = stack, Tag = r, ToolTip = r.Controller };
    }

    private static string ShortController(string c)
    {
        var m = Regex.Match(c, @"([^\\]+)$");
        return m.Success ? m.Groups[1].Value : c;
    }

    private Route? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Route;

    private void GoTo(object? _)
    {
        var r = Selected;
        if (r?.Class == null) return;
        if (!_ctx.Ide.GoToClass(r.Class, r.Method)) _ctx.Ide.Notify($"{r.Class} is not indexed yet.", NotificationKind.Warning);
    }

    private ContextMenu Menu(object? _)
    {
        var r = Selected!;
        var menu = new ContextMenu();
        void Add(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("Go to controller", () => GoTo(null), r.Class != null);
        var get = r.Methods.Length == 0 || r.Methods.Any(m => m.Equals("GET", StringComparison.OrdinalIgnoreCase));
        Add("Open in browser", () => OpenInBrowser(r), get);
        menu.Items.Add(new Separator());
        Add("Copy path", () => Clipboard.SetText(r.Path));
        Add("Copy name", () => Clipboard.SetText(r.Name), r.Name.Length > 0);
        Add("Copy controller", () => Clipboard.SetText(r.Controller), r.Controller.Length > 0);
        return menu;
    }

    private void OpenInBrowser(Route r)
    {
        var path = r.Path;
        if (r.HasParameters)
        {
            // ask a value for each {parameter}
            foreach (Match m in Regex.Matches(r.Path, @"\{(?<n>[^}:<]+)[^}]*\}"))
            {
                var v = _ctx.Ide.Prompt("Open in browser", $"Value of {{{m.Groups["n"].Value}}} in {r.Path}:");
                if (v == null) return;
                path = path.Replace(m.Value, Uri.EscapeDataString(v));
            }
        }
        var baseUrl = (_ctx.Ide.Project?.GetEnv("APP_URL") is { Length: > 0 } u ? u : "http://127.0.0.1:8000").TrimEnd('/');
        Process.Start(new ProcessStartInfo(baseUrl + path) { UseShellExecute = true });
    }
}
