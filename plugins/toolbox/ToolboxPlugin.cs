using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Toolbox;

public sealed class ToolboxPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "toolbox", Title = "NeoPHP", Icon = "", Location = ToolWindowLocation.Left,
            Create = () => new ToolboxView(context),
        });
        context.Ide.AddMenuItem("Run NeoPHP doctor", () => context.Ide.ShowToolWindow("toolbox"), "");
    }
}

/// <summary>Left tool window: collapsible sections Doctor / Migrations / Queue / Scheduler / Cache &amp; maintenance.</summary>
public sealed class ToolboxView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly IIde _ide;
    private readonly StackPanel _doctor = new(), _migrations = new(), _queue = new(), _schedule = new();
    private readonly TextBlock _doctorSummary = Ui.Text("", secondary: true, size: 11.5);
    private readonly TextBox _output = new()
    {
        IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, Visibility = Visibility.Collapsed,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6, 8, 6),
    };
    private bool _loaded;

    public ToolboxView(IPluginContext ctx)
    {
        _ctx = ctx;
        _ide = ctx.Ide;
        var bar = Ui.Toolbar(
            Ui.ToolButton("", "Refresh everything", async () => await RefreshAll()),
            Ui.ToolButton("", "Open the NeoConsole", () => _ide.RunNeoInConsole("list")));
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        SetDock(_output, Dock.Bottom);
        Children.Add(_output);

        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(Section("Doctor", "", Stack(_doctorSummary, _doctor),
            Ui.ToolButton("", "Run php bin/neo doctor", async () => await LoadDoctor())));
        content.Children.Add(Section("Migrations", "", _migrations,
            Ui.ToolButton("", "Migrate (migration:migrate)", () => Console("migration:migrate")),
            Ui.ToolButton("", "Roll back the last migration (migration:rollback)", () =>
            {
                if (_ide.Confirm("Roll back", "Roll back the last executed migrations? Their down() method is executed.", "Roll back")) Console("migration:rollback");
            }),
            Ui.ToolButton("", "New migration from the entities (make:migration)", () => Console("make:migration")),
            Ui.ToolButton("", "Refresh", async () => await LoadMigrations())));
        content.Children.Add(Section("Queue", "", _queue,
            Ui.ToolButton("", "Start a worker (queue:work)", () => Console("queue:work")),
            Ui.ToolButton("", "Failed messages (queue:failed)", () => Console("queue:failed")),
            Ui.ToolButton("", "Refresh", async () => await LoadQueue())));
        content.Children.Add(Section("Scheduler", "", _schedule,
            Ui.ToolButton("", "Run the due tasks (schedule:run)", async () => await Captured("schedule:run", LoadSchedule)),
            Ui.ToolButton("", "Run every minute in the foreground (schedule:work)", () => Console("schedule:work")),
            Ui.ToolButton("", "Refresh", async () => await LoadSchedule())));
        var actions = new WrapPanel { Margin = new Thickness(12, 4, 8, 4) };
        actions.Children.Add(Ui.Button("Clear cache", async () => await Captured("cache:clear", null)));
        actions.Children.Add(Ui.Button("Reload assets", async () => await Captured("asset:reload", null)));
        actions.Children.Add(Ui.Button("Maintenance on", async () => await Captured("down", null)));
        actions.Children.Add(Ui.Button("Maintenance off", async () => await Captured("up", null)));
        foreach (Button b in actions.Children) b.Margin = new Thickness(0, 0, 6, 6);
        content.Children.Add(Section("Cache & maintenance", "", actions));
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await RefreshAll(); } };
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { if (_loaded) await RefreshAll(); });
    }

    private static StackPanel Stack(params UIElement[] items)
    {
        var s = new StackPanel();
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    private static FrameworkElement Section(string title, string glyph, UIElement body, params UIElement[] buttons)
    {
        var chevron = Ui.Icon("", 9, "FgSecondary");
        chevron.Margin = new Thickness(0, 0, 8, 0);
        var head = new DockPanel { Height = 30, Margin = new Thickness(8, 6, 6, 0), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) right.Children.Add(b);
        SetDock(right, Dock.Right);
        head.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(chevron);
        var icon = Ui.Icon(glyph, 13, "Accent");
        icon.Margin = new Thickness(0, 0, 8, 0);
        left.Children.Add(icon);
        left.Children.Add(Ui.Title(title));
        head.Children.Add(left);
        var bodyHost = new Border { Child = body, Margin = new Thickness(0, 0, 0, 4) };
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, right)) return;
            var open = bodyHost.Visibility != Visibility.Visible;
            bodyHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "" : "";
        };
        var s = new StackPanel();
        s.Children.Add(head);
        s.Children.Add(bodyHost);
        return new Border { Child = s, BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, "SubtleBorder");
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == container) return true;
        return false;
    }

    private async Task RefreshAll()
    {
        if (_ide.Project == null) return;
        await Task.WhenAll(LoadDoctor(), LoadMigrations(), LoadQueue(), LoadSchedule());
    }

    private void Console(string command) => _ide.RunNeoInConsole(command);

    private async Task Captured(string command, Func<Task>? then)
    {
        _output.Visibility = Visibility.Visible;
        _output.Text = $"$ php bin/neo {command}\n…";
        var r = await _ide.RunNeoAsync(command);
        _output.Text = $"$ php bin/neo {command}   (exit {r.ExitCode})\n{(r.Output + r.Error).Trim()}";
        _ide.Notify($"{command}: {(r.Success ? "done" : "failed")}", r.Success ? NotificationKind.Success : NotificationKind.Error);
        if (then != null) await then();
    }

    // ------------------------------------------------------------------ sections

    private static TextBlock Line(string text, bool secondary = true)
    {
        var t = Ui.Text(text, secondary, 12);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(30, 2, 10, 2);
        return t;
    }

    private static string Note(string output)
    {
        var m = Regex.Match(output, @"\[(NOTE|OK|WARNING|ERROR|INFO)\]\s*(?<t>.+?)(\n\s*\n|$)", RegexOptions.Singleline);
        return m.Success ? Regex.Replace(m.Groups["t"].Value, @"\s+", " ").Trim() : output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
    }

    private FrameworkElement Row(string status, string color, string name, string detail, string? tooltip = null)
    {
        var dock = new DockPanel { Margin = new Thickness(30, 3, 10, 3), ToolTip = tooltip };
        var badge = Ui.Badge(status, Ui.Hex(color), 62);
        badge.VerticalAlignment = VerticalAlignment.Top;
        SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        var text = new StackPanel();
        var n = Ui.Text(name, size: 12.5);
        n.TextWrapping = TextWrapping.Wrap;
        n.TextTrimming = TextTrimming.None;
        text.Children.Add(n);
        if (detail.Length > 0)
        {
            var d = Ui.Text(detail, secondary: true, size: 11.5);
            d.TextWrapping = TextWrapping.Wrap;
            d.TextTrimming = TextTrimming.None;
            text.Children.Add(d);
        }
        dock.Children.Add(text);
        return dock;
    }

    private async Task LoadDoctor()
    {
        _doctor.Children.Clear();
        _doctorSummary.Text = "Running doctor…";
        _doctorSummary.Margin = new Thickness(30, 0, 10, 4);
        var r = await _ide.RunNeoAsync("doctor", 60_000);
        var (summary, checks) = Doctor.Parse(r.Output);
        if (checks.Count == 0) { _doctorSummary.Text = Note(r.Output + r.Error); return; }
        var problems = checks.Count(c => c.Status is "WARNING" or "ERROR");
        _doctorSummary.Text = $"{summary}{(summary.Length > 0 ? " · " : "")}{checks.Count(c => c.Status == "OK")} OK, {checks.Count(c => c.Status == "WARNING")} warning(s), {checks.Count(c => c.Status == "ERROR")} error(s)";
        // problems first, then everything grouped by section
        foreach (var c in checks.Where(c => c.Status is "ERROR" or "WARNING").Concat(checks.Where(c => c.Status is not ("ERROR" or "WARNING"))))
            _doctor.Children.Add(Row(c.Status, Doctor.Color(c.Status), $"{c.Section} › {c.Name}", c.Value + (c.Hints.Count > 0 ? "\n" + string.Join("\n", c.Hints) : "")));
        if (problems > 0) _ide.Notify($"NeoPHP doctor: {problems} problem(s) to check", NotificationKind.Warning);
    }

    private async Task LoadMigrations()
    {
        _migrations.Children.Clear();
        var r = await _ide.RunNeoAsync("migration:status");
        var rows = ConsoleTable.Parse(r.Output);
        if (rows.Count == 0) { _migrations.Children.Add(Line(Note(r.Output + r.Error))); return; }
        var pending = 0;
        foreach (var row in rows)
        {
            var status = row.GetValueOrDefault("Status") ?? "";
            var (label, color) = status.Contains("executed", StringComparison.OrdinalIgnoreCase) ? ("done", "#5FB865")
                : status.Contains("missing", StringComparison.OrdinalIgnoreCase) ? ("missing", "#E55B5B") : ("pending", "#E8A33D");
            if (label == "pending") pending++;
            var when = row.GetValueOrDefault("Executed at") ?? "";
            _migrations.Children.Add(Row(label, color, row.GetValueOrDefault("Migration") ?? "", ((row.GetValueOrDefault("Description") ?? "") + (when.Length > 0 ? "  ·  " + when : "")).Trim()));
        }
        _migrations.Children.Insert(0, Line(pending > 0 ? $"{pending} pending migration(s): click ▶ to migrate." : $"{rows.Count} migration(s), all executed."));
    }

    private async Task LoadQueue()
    {
        _queue.Children.Clear();
        var r = await _ide.RunNeoAsync("queue:status");
        var rows = ConsoleTable.Parse(r.Output);
        if (rows.Count == 0) { _queue.Children.Add(Line(Note(r.Output + r.Error))); return; }
        foreach (var row in rows)
        {
            var failed = row.GetValueOrDefault("Failed") ?? "-";
            var ready = row.GetValueOrDefault("Ready") ?? "-";
            var color = failed is not ("0" or "-" or "") ? "#E55B5B" : ready is not ("0" or "-" or "") ? "#E8A33D" : "#5FB865";
            _queue.Children.Add(Row(row.GetValueOrDefault("Transport") ?? "", color, $"queue {row.GetValueOrDefault("Queue")}",
                $"ready {ready} · delayed {row.GetValueOrDefault("Delayed")} · reserved {row.GetValueOrDefault("Reserved")} · failed {failed}"));
        }
        var def = Regex.Match(r.Output, @"Default transport:\s*(\S+)");
        if (def.Success) _queue.Children.Add(Line("Default transport: " + def.Groups[1].Value));
    }

    private async Task LoadSchedule()
    {
        _schedule.Children.Clear();
        var r = await _ide.RunNeoAsync("schedule:list");
        var rows = ConsoleTable.Parse(r.Output);
        if (rows.Count == 0) { _schedule.Children.Add(Line(Note(r.Output + r.Error))); return; }
        foreach (var row in rows)
        {
            var status = row.GetValueOrDefault("Status") ?? "";
            var color = status.Contains("fail", StringComparison.OrdinalIgnoreCase) ? "#E55B5B" : status.Contains("disabled", StringComparison.OrdinalIgnoreCase) ? "#868A91" : "#4C9BE8";
            _schedule.Children.Add(Row(row.GetValueOrDefault("Cron") ?? "", color, row.GetValueOrDefault("Task") ?? "",
                $"next {row.GetValueOrDefault("Next run")} · last {row.GetValueOrDefault("Last run")}{(status.Length > 0 ? " · " + status : "")}"));
        }
    }
}
