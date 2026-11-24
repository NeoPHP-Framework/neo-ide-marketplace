using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Todo;

public sealed record TodoItem(string File, int Line, string Tag, string Text);

public sealed class TodoPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Ide.AddToolWindow(new ToolWindow
    {
        Id = "todo", Title = "TODO", Icon = "", Location = ToolWindowLocation.Left,
        Create = () => new TodoView(context),
    });

    public static readonly string[] Folders = { "src", "templates", "config", "assets", "tests", "migrations", "translations", "public" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".php", ".twig", ".html", ".js", ".ts", ".jsx", ".tsx", ".vue", ".css", ".scss", ".yaml", ".yml", ".json", ".md", ".sql", ".xml", ".env" };
    private static readonly Regex Marker = new(@"(?:^|[^A-Za-z])(?<tag>TODO|FIXME|HACK|XXX|BUG)\b[:\s]*(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>Markers inside comments (//, #, /* */, *, {# #}, &lt;!-- --&gt;) of the project files.</summary>
    public static List<TodoItem> Scan(string root, CancellationToken ct)
    {
        var items = new List<TodoItem>();
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!Extensions.Contains(Path.GetExtension(file)) || file.Contains(Path.DirectorySeparatorChar + "builds" + Path.DirectorySeparatorChar)) continue;
                FileInfo info;
                try { info = new FileInfo(file); } catch { continue; }
                if (info.Length > 2 * 1024 * 1024) continue;
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { continue; }
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var comment = CommentStart(line);
                    if (comment < 0) continue;
                    var m = Marker.Match(line[comment..]);
                    if (!m.Success) continue;
                    var text = Regex.Replace(m.Groups["text"].Value, @"(\*/|#\}|-->)\s*$", "").Trim();
                    items.Add(new TodoItem(file, i + 1, m.Groups["tag"].Value, text));
                }
            }
        }
        return items;
    }

    private static int CommentStart(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith('*') || t.StartsWith('#')) return line.Length - t.Length;
        var best = -1;
        foreach (var token in new[] { "//", "/*", "{#", "<!--", " #" })
        {
            var i = line.IndexOf(token, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }
}

public sealed class TodoView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly TextBox _search = Ui.TextBox(tooltip: "Filter");
    private readonly TreeView _tree = new() { Padding = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _status = Ui.Text("", secondary: true, size: 11.5);
    private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> _tags = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private List<TodoItem> _items = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _scan;

    private static readonly Dictionary<string, string> Colors = new() { ["TODO"] = "#4C9BE8", ["FIXME"] = "#E55B5B", ["BUG"] = "#E55B5B", ["HACK"] = "#E8A33D", ["XXX"] = "#C77DBB" };

    public TodoView(IPluginContext ctx)
    {
        _ctx = ctx;
        var bar = new DockPanel { Margin = new Thickness(8, 4, 6, 4) };
        var refresh = Ui.ToolButton("", "Rescan", async () => await Rescan());
        SetDock(refresh, Dock.Right);
        bar.Children.Add(refresh);
        bar.Children.Add(_search);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var tags = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
        foreach (var tag in new[] { "TODO", "FIXME", "HACK", "XXX" })
        {
            var t = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = new TextBlock { Text = tag, FontWeight = FontWeights.SemiBold, FontSize = 11.5, Foreground = new System.Windows.Media.SolidColorBrush(Ui.Hex(Colors[tag])) },
                IsChecked = true, Style = Ui.Style("ModeToggle"), Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(7, 2, 7, 2),
            };
            t.Click += (_, _) => Fill();
            _tags[tag] = t;
            tags.Children.Add(t);
        }
        SetDock(tags, Dock.Top);
        Children.Add(tags);
        var statusBox = new Border { Padding = new Thickness(10, 0, 10, 2), Child = _status };
        SetDock(statusBox, Dock.Top);
        Children.Add(statusBox);
        Children.Add(_tree);
        _search.TextChanged += (_, _) => Fill();
        _tree.MouseDoubleClick += (_, e) => { if (Open()) e.Handled = true; };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) Open(); };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Rescan(); };
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { Watch(); await Rescan(); });
        Loaded += async (_, _) => { if (_watcher == null) { Watch(); await Rescan(); } };
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_ctx.Ide.Project is not { } p) return;
        _watcher = new FileSystemWatcher(p.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        FileSystemEventHandler h = (_, e) =>
        {
            var rel = Path.GetRelativePath(p.Root, e.FullPath);
            var first = rel.Split(Path.DirectorySeparatorChar)[0];
            if (TodoPlugin.Folders.Contains(first)) Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        };
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Deleted += h;
        _watcher.Renamed += (s, e) => h(s, e);
    }

    private async Task Rescan()
    {
        if (_ctx.Ide.Project is not { } p) { _status.Text = "No project"; return; }
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        _status.Text = "Scanning…";
        try
        {
            _items = await Task.Run(() => TodoPlugin.Scan(p.Root, cts.Token));
            Fill();
        }
        catch (OperationCanceledException) { }
    }

    private void Fill()
    {
        if (_ctx.Ide.Project is not { } p) return;
        var q = _search.Text.Trim();
        var shown = _items.Where(i => _tags.TryGetValue(i.Tag == "BUG" ? "FIXME" : i.Tag, out var t) && t.IsChecked == true)
            .Where(i => q.Length == 0 || i.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || i.File.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _tree.Items.Clear();
        foreach (var group in shown.GroupBy(i => i.File).OrderBy(g => g.Key))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(Ui.Text(Path.GetFileName(group.Key)));
            header.Children.Add(Ui.Text("  " + Path.GetDirectoryName(Path.GetRelativePath(p.Root, group.Key)), secondary: true, size: 11.5));
            header.Children.Add(Ui.Text($"  {group.Count()}", secondary: true, size: 11.5));
            var node = new TreeViewItem { Header = header, IsExpanded = true, ToolTip = group.Key };
            foreach (var item in group)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Ui.Badge(item.Tag, Ui.Hex(Colors.GetValueOrDefault(item.Tag, "#868A91")), 44));
                row.Children.Add(Ui.Text(item.Text.Length > 0 ? item.Text : "(no description)", size: 12));
                row.Children.Add(Ui.Text($"  :{item.Line}", secondary: true, size: 11.5));
                node.Items.Add(new TreeViewItem { Header = row, Tag = item, ToolTip = item.Text });
            }
            _tree.Items.Add(node);
        }
        var files = shown.Select(i => i.File).Distinct().Count();
        _status.Text = shown.Count == 0 ? (_items.Count == 0 ? "No TODO in the project 🎉" : "Nothing matches the filter") : $"{shown.Count} item(s) in {files} file(s)";
    }

    private bool Open()
    {
        if (_tree.SelectedItem is not TreeViewItem { Tag: TodoItem item }) return false;
        _ctx.Ide.OpenFile(item.File, item.Line);
        return true;
    }
}
