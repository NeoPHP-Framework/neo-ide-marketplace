using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Container;

public sealed record Service(string Id, string Kind, string Class, string Resolved);
public sealed record Listener(string Event, int Order, string Callable, string Priority);

public sealed class ContainerPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Ide.AddToolWindow(new ToolWindow
    {
        Id = "container", Title = "Container & Events", Icon = "", Location = ToolWindowLocation.Left,
        Create = () => new ContainerView(context),
    });

    /// <summary>Rows of the first box table, without merging the rows whose first cell is empty (event:list groups by event).</summary>
    public static List<string[]> Rows(string output, out string[] header)
    {
        header = Array.Empty<string>();
        var rows = new List<string[]>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (!t.StartsWith('|')) { if (rows.Count > 0 && !t.StartsWith('+')) break; continue; }
            var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (header.Length == 0) header = cells; else rows.Add(cells);
        }
        return rows;
    }

    public static List<Service> ParseServices(string output)
    {
        var rows = Rows(output, out var h);
        int I(string name) => Array.FindIndex(h, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        int id = I("Id"), kind = I("Kind"), cls = I("Class"), res = I("Resolved");
        string Get(string[] r, int i) => i >= 0 && i < r.Length ? r[i] : "";
        return rows.Where(r => Get(r, id).Length > 0).Select(r => new Service(Get(r, id), Get(r, kind), Get(r, cls), Get(r, res))).ToList();
    }

    public static List<Listener> ParseListeners(string output)
    {
        var rows = Rows(output, out var h);
        var list = new List<Listener>();
        var current = "";
        foreach (var r in rows)
        {
            if (r.Length < 3) continue;
            if (r[0].Length > 0) current = r[0];
            if (r[2].Length == 0) continue;
            list.Add(new Listener(current, int.TryParse(r[1], out var n) ? n : 0, r[2], r.Length > 3 ? r[3] : ""));
        }
        return list;
    }

    /// <summary>"Vendor\Class::method()" → (class, method).</summary>
    public static (string? cls, string? method) Split(string callable)
    {
        var m = Regex.Match(callable, @"^@?(?<c>[\w\\]+)(::(?<m>\w+))?");
        return m.Success ? (m.Groups["c"].Value, m.Groups["m"].Success ? m.Groups["m"].Value : null) : (null, null);
    }
}

public sealed class ContainerView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly TextBox _search = Ui.TextBox(tooltip: "Filter by id, class, event or listener");
    private readonly ListBox _services = new(), _events = new();
    private readonly TextBlock _status = Ui.Text("", secondary: true, size: 11.5);
    private readonly TabControl _tabs = new() { Style = Ui.Style("ToolTabControl"), BorderThickness = new Thickness(0) };
    private List<Service> _serviceList = new();
    private List<Listener> _listenerList = new();
    private bool _loaded;

    public ContainerView(IPluginContext ctx)
    {
        _ctx = ctx;
        var bar = new DockPanel { Margin = new Thickness(8, 4, 6, 4) };
        var refresh = Ui.ToolButton("", "Reload (debug:container, event:list)", async () => await Load());
        SetDock(refresh, Dock.Right);
        bar.Children.Add(refresh);
        bar.Children.Add(_search);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var statusBox = new Border { Padding = new Thickness(10, 0, 10, 2), Child = _status };
        SetDock(statusBox, Dock.Top);
        Children.Add(statusBox);
        _tabs.Items.Add(new TabItem { Header = "Services", Content = _services, Style = Ui.Style("ToolTabItem") });
        _tabs.Items.Add(new TabItem { Header = "Events", Content = _events, Style = Ui.Style("ToolTabItem") });
        _tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == _tabs) Fill(); };
        Children.Add(_tabs);
        _search.TextChanged += (_, _) => Fill();
        foreach (var list in new[] { _services, _events })
        {
            list.MouseDoubleClick += (_, _) => Open(list);
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Open(list); };
        }
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { if (_loaded) await Load(); });
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await Load(); } };
    }

    private async Task Load()
    {
        if (_ctx.Ide.Project == null) { _status.Text = "No project"; return; }
        _status.Text = "Loading…";
        var services = _ctx.Ide.RunNeoAsync("debug:container");
        var events = _ctx.Ide.RunNeoAsync("event:list");
        var s = await services;
        var e = await events;
        _serviceList = ContainerPlugin.ParseServices(s.Output);
        _listenerList = ContainerPlugin.ParseListeners(e.Output);
        if (!s.Success && _serviceList.Count == 0) _status.Text = "debug:container failed: " + (s.Error.Trim().Length > 0 ? s.Error.Trim() : s.Output.Trim());
        Fill();
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        bool M(string x) => q.Length == 0 || x.Contains(q, StringComparison.OrdinalIgnoreCase);
        if (_tabs.SelectedIndex == 0)
        {
            var shown = _serviceList.Where(s => M(s.Id) || M(s.Class) || M(s.Kind)).ToList();
            _services.Items.Clear();
            foreach (var s in shown) _services.Items.Add(ServiceRow(s));
            _status.Text = $"{shown.Count} of {_serviceList.Count} service(s)";
        }
        else
        {
            var shown = _listenerList.Where(l => M(l.Event) || M(l.Callable)).ToList();
            _events.Items.Clear();
            foreach (var group in shown.GroupBy(l => l.Event))
            {
                var head = Ui.Text(Short(group.Key), size: 12.5);
                head.FontWeight = FontWeights.SemiBold;
                head.ToolTip = group.Key;
                _events.Items.Add(new ListBoxItem { Content = head, Tag = new Listener(group.Key, 0, group.Key, ""), Margin = new Thickness(0, 6, 0, 0) });
                foreach (var l in group) _events.Items.Add(ListenerRow(l));
            }
            _status.Text = $"{shown.Select(l => l.Event).Distinct().Count()} event(s), {shown.Count} listener(s)";
        }
    }

    private static string Short(string fqcn) => fqcn.Contains('\\') ? fqcn[(fqcn.LastIndexOf('\\') + 1)..] : fqcn;

    private static readonly Dictionary<string, string> KindColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["singleton"] = "#4C9BE8", ["instance"] = "#5FB865", ["alias"] = "#868A91", ["factory"] = "#E8A33D", ["transient"] = "#C77DBB",
    };

    private static ListBoxItem ServiceRow(Service s)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 2, 2, 2) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(Ui.Badge(s.Kind.Length > 0 ? s.Kind : "service", Ui.Hex(KindColors.GetValueOrDefault(s.Kind, "#868A91")), 62));
        top.Children.Add(Ui.Text(Short(s.Id)));
        stack.Children.Add(top);
        var detail = s.Class.StartsWith('@') ? "→ " + s.Class[1..] : s.Class == s.Id ? s.Id : $"{s.Id}  →  {s.Class}";
        stack.Children.Add(Ui.Text(detail + (s.Resolved == "yes" ? "  · resolved" : ""), secondary: true, size: 11.5));
        return new ListBoxItem { Content = stack, Tag = s, ToolTip = s.Id };
    }

    private static ListBoxItem ListenerRow(Listener l)
    {
        var dock = new DockPanel { Margin = new Thickness(14, 1, 2, 1) };
        var prio = Ui.Text(l.Priority, secondary: true, size: 11.5);
        prio.ToolTip = "Priority (higher = called first)";
        SetDock(prio, Dock.Right);
        dock.Children.Add(prio);
        var n = Ui.Text($"{l.Order}. ", secondary: true, size: 12);
        SetDock(n, Dock.Left);
        dock.Children.Add(n);
        var (cls, method) = ContainerPlugin.Split(l.Callable);
        dock.Children.Add(Ui.Text(cls != null ? Short(cls) + (method != null ? "::" + method + "()" : "") : l.Callable, size: 12));
        return new ListBoxItem { Content = dock, Tag = l, ToolTip = l.Callable };
    }

    private void Open(ListBox list)
    {
        string? cls = null, member = null;
        switch ((list.SelectedItem as ListBoxItem)?.Tag)
        {
            case Service s:
                cls = s.Class.StartsWith('@') ? s.Class[1..] : s.Class is "closure" or "" ? s.Id : s.Class;
                break;
            case Listener l:
                (cls, member) = ContainerPlugin.Split(l.Callable);
                break;
        }
        if (cls == null) return;
        if (!_ctx.Ide.GoToClass(cls, member)) _ctx.Ide.Notify($"{cls} is not in the PHP index.", NotificationKind.Warning);
    }
}
