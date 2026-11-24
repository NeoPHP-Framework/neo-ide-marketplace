using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Env;

public sealed record EnvVar(string Name, string Value, string File, List<string> OverriddenIn, string? Comment)
{
    public bool IsSecret => Regex.IsMatch(Name, "SECRET|PASSWORD|PASSWD|TOKEN|KEY|PRIVATE|DSN|DATABASE_URL", RegexOptions.IgnoreCase) && Value.Length > 0;
}

public sealed class EnvPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Ide.AddToolWindow(new ToolWindow
    {
        Id = "env", Title = "Environment", Icon = "", Location = ToolWindowLocation.Left,
        Create = () => new EnvView(context),
    });

    public static string[] Files(string root, string appEnv) => new[] { ".env", ".env.local", $".env.{appEnv}", $".env.{appEnv}.local" };

    /// <summary>Variables in the load order of NeoPHP, with the file that gives the final value.</summary>
    public static List<EnvVar> Load(string root)
    {
        var defined = new Dictionary<string, List<(string file, string value)>>();
        var comments = new Dictionary<string, string>();
        var known = new Dictionary<string, string>();
        void Read(string file)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            foreach (var (k, v) in DotEnv.Parse(text, known))
            {
                known[k] = v;
                if (!defined.TryGetValue(k, out var l)) defined[k] = l = new();
                l.Add((file, v));
            }
            // the comment line(s) right above a variable explain it
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"^\s*(export\s+)?(?<k>[A-Za-z_][A-Za-z0-9_]*)\s*=");
                if (!m.Success || comments.ContainsKey(m.Groups["k"].Value)) continue;
                var c = new List<string>();
                for (int j = i - 1; j >= 0 && lines[j].TrimStart().StartsWith('#') && !lines[j].Contains("###"); j--) c.Insert(0, lines[j].TrimStart('#', ' '));
                if (c.Count > 0) comments[m.Groups["k"].Value] = string.Join("\n", c);
            }
        }
        Read(".env");
        Read(".env.local");
        var appEnv = known.TryGetValue("APP_ENV", out var e) && e.Length > 0 ? e : "dev";
        Read($".env.{appEnv}");
        Read($".env.{appEnv}.local");
        var rootPath = root.Replace('\\', '/').TrimEnd('/');
        return defined.Select(kv =>
        {
            var last = kv.Value[^1];
            var value = last.value.Replace("%kernel.root_path%", rootPath);
            return new EnvVar(kv.Key, value, last.file, kv.Value.Take(kv.Value.Count - 1).Select(x => x.file).ToList(), comments.GetValueOrDefault(kv.Key));
        }).ToList();
    }

    /// <summary>Writes NAME=value in .env.local (replaces the line when it exists, appends otherwise).</summary>
    public static void SetLocal(string root, string name, string value)
    {
        var path = Path.Combine(root, ".env.local");
        var quoted = Regex.IsMatch(value, @"[\s#""'$\\]") ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;
        var line = $"{name}={quoted}";
        var lines = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList() : new List<string> { "# Local overrides of .env: NOT committed (machine-specific values, real secrets)" };
        var index = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*(export\s+)?{Regex.Escape(name)}\s*="));
        if (index >= 0) lines[index] = line;
        else
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(line);
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }
}

public sealed class EnvView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly TextBox _search = Ui.TextBox(tooltip: "Filter by name or value");
    private readonly CheckBox _reveal = new() { Content = "Show secrets", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _list = new();
    private readonly TextBlock _status = Ui.Text("", secondary: true, size: 11.5);
    private List<EnvVar> _vars = new();
    private FileSystemWatcher? _watcher;

    public EnvView(IPluginContext ctx)
    {
        _ctx = ctx;
        var bar = new DockPanel { Margin = new Thickness(8, 4, 6, 4) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Ui.ToolButton("", "Reload", Reload));
        right.Children.Add(Ui.ToolButton("", "Open .env.local", () => OpenFile(".env.local", create: true)));
        SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_search);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var options = new DockPanel { Margin = new Thickness(10, 0, 10, 4) };
        SetDock(_reveal, Dock.Right);
        options.Children.Add(_reveal);
        options.Children.Add(_status);
        SetDock(options, Dock.Top);
        Children.Add(options);
        Children.Add(_list);
        _search.TextChanged += (_, _) => Fill();
        _reveal.Click += (_, _) => Fill();
        _list.MouseDoubleClick += (_, _) => Edit();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Edit(); };
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, e) => { if (Selected == null) e.Handled = true; else _list.ContextMenu = Menu(Selected); };
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(() => { Watch(); Reload(); });
        Loaded += (_, _) => { if (_vars.Count == 0) { Watch(); Reload(); } };
    }

    private EnvVar? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as EnvVar;

    private void Watch()
    {
        _watcher?.Dispose();
        if (_ctx.Ide.Project is not { } p) return;
        _watcher = new FileSystemWatcher(p.Root, ".env*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _ctx.Ide.Project?.ReloadEnv(); Reload(); });
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Deleted += h;
    }

    private void Reload()
    {
        if (_ctx.Ide.Project is not { } p) { _vars = new(); _status.Text = "No project"; Fill(); return; }
        try { _vars = EnvPlugin.Load(p.Root); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        Fill();
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        var shown = _vars.Where(v => q.Length == 0 || v.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || (!v.IsSecret || _reveal.IsChecked == true) && v.Value.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.Items.Clear();
        foreach (var v in shown) _list.Items.Add(Row(v));
        var local = _vars.Count(v => v.File != ".env");
        _status.Text = $"{shown.Count} of {_vars.Count} variable(s) · {local} overridden locally";
    }

    private ListBoxItem Row(EnvVar v)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 2, 2, 3) };
        var top = new DockPanel();
        var file = Ui.Badge(v.File, Ui.Hex(v.File == ".env" ? "#868A91" : v.File.EndsWith(".local") ? "#E8A33D" : "#4C9BE8"));
        file.Margin = new Thickness(6, 0, 0, 0);
        SetDock(file, Dock.Right);
        top.Children.Add(file);
        var name = Ui.Text(v.Name);
        name.FontFamily = Ui.Mono;
        name.FontWeight = FontWeights.SemiBold;
        top.Children.Add(name);
        stack.Children.Add(top);
        var shown = v.Value.Length == 0 ? "(empty)" : v.IsSecret && _reveal.IsChecked != true ? "••••••••" + (v.Value.Length > 4 ? $"  ({v.Value.Length} chars)" : "") : v.Value;
        var value = Ui.Text(shown, secondary: true, size: 12);
        value.FontFamily = Ui.Mono;
        stack.Children.Add(value);
        var tip = (v.Comment ?? "") + (v.OverriddenIn.Count > 0 ? $"{(v.Comment != null ? "\n\n" : "")}Overrides the value of {string.Join(", ", v.OverriddenIn)}" : "");
        return new ListBoxItem { Content = stack, Tag = v, ToolTip = tip.Length > 0 ? tip : null };
    }

    private ContextMenu Menu(EnvVar v)
    {
        var menu = new ContextMenu();
        void Add(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("Edit in .env.local…", Edit);
        Add("Copy value", () => Clipboard.SetText(v.Value));
        Add("Copy name", () => Clipboard.SetText(v.Name));
        menu.Items.Add(new Separator());
        Add($"Open {v.File}", () => OpenFile(v.File, create: false, variable: v.Name));
        return menu;
    }

    private void Edit()
    {
        if (Selected is not { } v || _ctx.Ide.Project is not { } p) return;
        var value = _ctx.Ide.Prompt($"{v.Name} in .env.local", $"Value of {v.Name} (written to .env.local, not committed):", v.Value);
        if (value == null || value == v.Value) return;
        try
        {
            EnvPlugin.SetLocal(p.Root, v.Name, value);
            p.ReloadEnv();
            Reload();
            _ctx.Ide.Notify($"{v.Name} set in .env.local", NotificationKind.Success);
        }
        catch (Exception ex) { _ctx.Ide.Notify(ex.Message, NotificationKind.Error); }
    }

    private void OpenFile(string file, bool create, string? variable = null)
    {
        if (_ctx.Ide.Project is not { } p) return;
        var path = Path.Combine(p.Root, file);
        if (!File.Exists(path))
        {
            if (!create) return;
            File.WriteAllText(path, "# Local overrides of .env: NOT committed (machine-specific values, real secrets)\n", new UTF8Encoding(false));
        }
        var line = 0;
        if (variable != null)
        {
            var lines = File.ReadAllLines(path);
            line = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^\s*(export\s+)?{Regex.Escape(variable)}\s*=")) + 1;
        }
        _ctx.Ide.OpenFile(path, line);
    }
}
