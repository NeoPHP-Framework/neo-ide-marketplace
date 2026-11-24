using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NeoIDE.Plugins.Shared;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.ComposerAudit;

public sealed record Advisory(string Package, string Title, string Severity, string? Cve, string? Link, string Affected);
public sealed record Outdated(string Name, string Version, string Latest, string Status, string Description, bool Abandoned, string? Replacement);

public sealed class ComposerAuditPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Ide.AddToolWindow(new ToolWindow
    {
        Id = "composer-audit", Title = "Composer Audit", Icon = "", Location = ToolWindowLocation.Left,
        Create = () => new AuditView(context),
    });

    /// <summary>"advisories" is an object { package: [ … ] } (or [] when empty).</summary>
    public static List<Advisory> ParseAudit(string json)
    {
        var list = new List<Advisory>();
        if (Parse(json)?["advisories"] is not JsonObject adv) return list;
        foreach (var (pkg, node) in adv)
        {
            var items = node is JsonArray a ? a.OfType<JsonObject>() : node is JsonObject o ? o.Select(kv => kv.Value).OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
            foreach (var i in items)
                list.Add(new Advisory(pkg, S(i["title"]) ?? "Security advisory", S(i["severity"]) ?? "unknown", S(i["cve"]), S(i["link"]), S(i["affectedVersions"]) ?? ""));
        }
        return list;
    }

    public static List<string> ParseAbandonedFromAudit(string json) =>
        Parse(json)?["abandoned"] is JsonObject ab ? ab.Select(kv => kv.Key).ToList() : new();

    public static List<Outdated> ParseOutdated(string json)
    {
        var list = new List<Outdated>();
        if (Parse(json)?["installed"] is not JsonArray installed) return list;
        foreach (var i in installed.OfType<JsonObject>())
        {
            var abandoned = i["abandoned"] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && s.Length > 0);
            list.Add(new Outdated(S(i["name"]) ?? "", S(i["version"]) ?? "", S(i["latest"]) ?? "", S(i["latest-status"]) ?? "", S(i["description"]) ?? "",
                abandoned, i["abandoned"] is JsonValue rv && rv.TryGetValue<string>(out var r) ? r : null));
        }
        return list;
    }

    /// <summary>The JSON object of the output (composer may print warnings before it).</summary>
    private static JsonObject? Parse(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) return null;
        try { return JsonNode.Parse(output[start..]) as JsonObject; } catch (JsonException) { return null; }
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

public sealed class AuditView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly StackPanel _security = new(), _outdated = new();
    private readonly TextBlock _summary = Ui.Text("", secondary: true, size: 11.5);
    private readonly CheckBox _all = new() { Content = "Include indirect dependencies", Margin = new Thickness(10, 2, 10, 4), FontSize = 12 };
    private readonly OutputPane _output = new() { Height = 150, Visibility = Visibility.Collapsed };
    private readonly Proc _proc = new();
    private bool _busy, _loaded;

    public AuditView(IPluginContext ctx)
    {
        _ctx = ctx;
        var bar = Ui.Toolbar(
            Ui.ToolButton("", "Check again", async () => await Load()),
            Ui.ToolButton("", "Apply every safe update (composer update of the packages with a compatible new version)", async () => await UpdateSafe()),
            Ui.ToolButton("", "Open composer.json", () => { if (ctx.Ide.Project is { } p) ctx.Ide.OpenFile(System.IO.Path.Combine(p.Root, "composer.json")); }),
            Ui.ToolButton("", "Show / hide the composer output", () => _output.Visibility = _output.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var summaryBox = new Border { Padding = new Thickness(10, 4, 10, 0), Child = _summary };
        _summary.TextWrapping = TextWrapping.Wrap;
        SetDock(summaryBox, Dock.Top);
        Children.Add(summaryBox);
        SetDock(_all, Dock.Top);
        Children.Add(_all);
        _all.Click += async (_, _) => await Load();
        var outputBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _output }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        SetDock(outputBorder, Dock.Bottom);
        Children.Add(outputBorder);
        var content = new StackPanel();
        content.Children.Add(Section("Security", ""));
        content.Children.Add(_security);
        content.Children.Add(Section("Outdated packages", ""));
        content.Children.Add(_outdated);
        Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { if (_loaded) await Load(); });
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await Load(); } };
        Unloaded += (_, _) => _proc.Stop();
    }

    private static FrameworkElement Section(string title, string glyph)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 12, 10, 4) };
        var icon = Ui.Icon(glyph, 13, "Accent");
        icon.Margin = new Thickness(0, 0, 8, 0);
        p.Children.Add(icon);
        p.Children.Add(Ui.Title(title));
        return p;
    }

    private static TextBlock Line(string text)
    {
        var t = Ui.Text(text, secondary: true, size: 12);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(32, 2, 10, 4);
        return t;
    }

    private async Task<(int code, string output)> Composer(string args, bool show = false)
    {
        if (_ctx.Ide.Project is not { } p) return (-1, "No project");
        var phar = Proc.ComposerPhar(p.Root);
        if (phar == null) return (-1, "composer.phar not found (install Composer: https://getcomposer.org).");
        if (show)
        {
            _output.Visibility = Visibility.Visible;
            _output.Reset($"$ composer {args}");
        }
        return await _proc.RunAsync(Proc.Php(p), $"{Proc.Quote(phar)} {args}", p.Root, show ? _output.Append : null);
    }

    private async Task Load()
    {
        if (_busy || _ctx.Ide.Project == null) return;
        _busy = true;
        _summary.Text = "Checking the dependencies (composer audit, composer outdated)…";
        _security.Children.Clear();
        _outdated.Children.Clear();
        try
        {
            var audit = await Composer("audit --format=json --no-interaction");
            var outdated = await Composer($"outdated {(_all.IsChecked == true ? "" : "--direct ")}--format=json --no-interaction");
            var advisories = ComposerAuditPlugin.ParseAudit(audit.output);
            var packages = ComposerAuditPlugin.ParseOutdated(outdated.output);
            var abandoned = ComposerAuditPlugin.ParseAbandonedFromAudit(audit.output);

            if (audit.output.IndexOf('{') < 0) _security.Children.Add(Line("composer audit failed: " + audit.output.Trim()));
            else if (advisories.Count == 0) _security.Children.Add(Line("No known vulnerability in the installed packages."));
            foreach (var a in advisories.OrderBy(a => SeverityRank(a.Severity))) _security.Children.Add(AdvisoryRow(a));
            foreach (var name in abandoned) _security.Children.Add(Row("abandoned", "#868A91", name, "This package is not maintained any more: look for its replacement.", null));

            if (outdated.output.IndexOf('{') < 0) _outdated.Children.Add(Line("composer outdated failed: " + outdated.output.Trim()));
            else if (packages.Count == 0) _outdated.Children.Add(Line("Every package is up to date."));
            foreach (var o in packages.OrderBy(o => o.Status == "semver-safe-update" ? 0 : 1).ThenBy(o => o.Name)) _outdated.Children.Add(OutdatedRow(o));

            var safe = packages.Count(o => o.Status == "semver-safe-update");
            _summary.Text = $"{advisories.Count} vulnerability(ies) · {packages.Count} outdated package(s), {safe} safe update(s) · {abandoned.Count} abandoned";
            if (advisories.Count > 0) _ctx.Ide.Notify($"composer audit: {advisories.Count} vulnerability(ies) in your dependencies", NotificationKind.Warning);
        }
        finally { _busy = false; }
    }

    private static int SeverityRank(string s) => s.ToLowerInvariant() switch { "critical" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4 };

    private FrameworkElement Row(string badge, string color, string title, string detail, Button? action)
    {
        var dock = new DockPanel { Margin = new Thickness(12, 3, 10, 5) };
        var b = Ui.Badge(badge, Ui.Hex(color), 74);
        b.VerticalAlignment = VerticalAlignment.Top;
        SetDock(b, Dock.Left);
        dock.Children.Add(b);
        if (action != null)
        {
            action.VerticalAlignment = VerticalAlignment.Top;
            action.Padding = new Thickness(8, 1, 8, 1);
            action.Margin = new Thickness(6, 0, 0, 0);
            SetDock(action, Dock.Right);
            dock.Children.Add(action);
        }
        var text = new StackPanel();
        var t = Ui.Text(title, size: 12.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        text.Children.Add(t);
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

    private FrameworkElement AdvisoryRow(Advisory a)
    {
        var color = SeverityRank(a.Severity) switch { 0 => "#D63A8A", 1 => "#E55B5B", 2 => "#E8A33D", 3 => "#4C9BE8", _ => "#868A91" };
        Button? open = null;
        if (a.Link != null)
        {
            open = Ui.Button("Details", () => Process.Start(new ProcessStartInfo(a.Link) { UseShellExecute = true }));
        }
        return Row(a.Severity, color, $"{a.Package}: {a.Title}", $"{(a.Cve != null ? a.Cve + " · " : "")}affected {a.Affected}", open);
    }

    private FrameworkElement OutdatedRow(Outdated o)
    {
        var safe = o.Status == "semver-safe-update";
        var update = Ui.Button("Update", async () =>
        {
            var hint = safe ? "" : $"\n\n{o.Latest} is a new major version: composer only updates within the constraint of composer.json. Change the constraint to move to {o.Latest}.";
            if (!_ctx.Ide.Confirm("composer update", $"Update {o.Name} (and its dependencies)?{hint}", "Update")) return;
            var r = await Composer($"update {o.Name} --with-dependencies --no-interaction", show: true);
            _ctx.Ide.Notify(r.code == 0 ? $"{o.Name} updated" : $"composer update {o.Name} failed", r.code == 0 ? NotificationKind.Success : NotificationKind.Error);
            await Load();
        });
        return Row(safe ? "update" : "major", safe ? "#5FB865" : "#E8A33D", $"{o.Name}  {o.Version} → {o.Latest}",
            (o.Abandoned ? "ABANDONED" + (o.Replacement != null ? $", use {o.Replacement}" : "") + " · " : "") + o.Description, update);
    }

    private async Task UpdateSafe()
    {
        var r0 = await Composer($"outdated {(_all.IsChecked == true ? "" : "--direct ")}--format=json --no-interaction");
        var safe = ComposerAuditPlugin.ParseOutdated(r0.output).Where(o => o.Status == "semver-safe-update").Select(o => o.Name).ToList();
        if (safe.Count == 0) { _ctx.Ide.Notify("No safe update available.", NotificationKind.Info); return; }
        if (!_ctx.Ide.Confirm("composer update", $"Update {safe.Count} package(s)?\n\n{string.Join("\n", safe)}", "Update")) return;
        var r = await Composer($"update {string.Join(" ", safe)} --with-dependencies --no-interaction", show: true);
        _ctx.Ide.Notify(r.code == 0 ? $"{safe.Count} package(s) updated" : "composer update failed", r.code == 0 ? NotificationKind.Success : NotificationKind.Error);
        await Load();
    }
}
