using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Plugins.Shared;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.CodeQuality;

public sealed record Problem(string File, int Line, string Message, string? Identifier, string? Tip);
public sealed record StyleFix(string File, List<string> Fixers, string Diff);

public sealed class CodeQualityPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "code-quality", Title = "Code Quality", Icon = "", Location = ToolWindowLocation.Bottom,
            Create = () => new QualityView(context),
        });
        context.Ide.AddMenuItem("Run PHPStan", () => { context.Ide.ShowToolWindow("code-quality"); QualityView.Current?.RunPhpStanFromMenu(); }, "");
    }

    public static string? PhpStanScript(string root) => First(root, "vendor/phpstan/phpstan/phpstan", "vendor/bin/phpstan");
    public static string? CsFixerScript(string root) => First(root, "vendor/friendsofphp/php-cs-fixer/php-cs-fixer", "vendor/bin/php-cs-fixer");
    public static string? PhpStanConfig(string root) => First(root, "phpstan.neon", "phpstan.neon.dist", "phpstan.dist.neon");
    public static string? CsFixerConfig(string root) => First(root, ".php-cs-fixer.php", ".php-cs-fixer.dist.php");

    private static string? First(string root, params string[] candidates) =>
        candidates.Select(c => Path.Combine(root, c.Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(File.Exists);

    private static JsonObject? Json(string output)
    {
        var start = output.IndexOf('{');
        while (start >= 0)
        {
            try { return JsonNode.Parse(output[start..]) as JsonObject; }
            catch (JsonException) { start = output.IndexOf('{', start + 1); }
        }
        return null;
    }

    /// <summary>PHPStan --error-format=json: files { path: { messages: [ { message, line, identifier, tip } ] } } + errors [ "…" ].</summary>
    public static (List<Problem> problems, List<string> general, bool parsed) ParsePhpStan(string output)
    {
        var problems = new List<Problem>();
        var general = new List<string>();
        var doc = Json(output);
        if (doc == null) return (problems, general, false);
        if (doc["files"] is JsonObject files)
            foreach (var (file, node) in files)
                if (node?["messages"] is JsonArray messages)
                    foreach (var m in messages.OfType<JsonObject>())
                        problems.Add(new Problem(file, m["line"]?.GetValue<int>() ?? 0, m["message"]?.GetValue<string>() ?? "", m["identifier"]?.GetValue<string>(), m["tip"]?.GetValue<string>()));
        if (doc["errors"] is JsonArray errors) general.AddRange(errors.Select(e => e?.ToString() ?? "").Where(e => e.Length > 0));
        return (problems, general, true);
    }

    /// <summary>php-cs-fixer --format=json: files [ { name, appliedFixers, diff } ].</summary>
    public static (List<StyleFix> fixes, bool parsed) ParseCsFixer(string output, string root)
    {
        var list = new List<StyleFix>();
        var doc = Json(output);
        if (doc == null) return (list, false);
        if (doc["files"] is JsonArray files)
            foreach (var f in files.OfType<JsonObject>())
            {
                var name = f["name"]?.GetValue<string>() ?? "";
                var path = Path.IsPathRooted(name) ? name : Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                var fixers = f["appliedFixers"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : new List<string>();
                list.Add(new StyleFix(path, fixers, f["diff"]?.GetValue<string>() ?? ""));
            }
        return (list, true);
    }
}

public sealed class QualityView : DockPanel
{
    internal static QualityView? Current;
    private readonly IPluginContext _ctx;
    private readonly ComboBox _level = new() { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _paths = Ui.TextBox("src", 120, "Paths to analyse (space separated), when there is no phpstan.neon");
    private readonly TreeView _tree = new() { Padding = new Thickness(0, 2, 0, 6) };
    private readonly ICodeEditor _detail;
    private readonly OutputPane _output = new();
    private readonly TextBlock _status = Ui.Text("", secondary: true);
    private readonly TextBlock _levelLabel = Ui.Text("Level ", secondary: true);
    private readonly Button _phpstan, _cs, _fix, _installStan, _installCs;
    private readonly TabControl _right = new() { Style = Ui.Style("ToolTabControl"), BorderThickness = new Thickness(0) };
    private readonly Proc _proc = new();
    private List<StyleFix> _fixes = new();

    public QualityView(IPluginContext ctx)
    {
        Current = this;
        _ctx = ctx;
        _detail = ctx.Ide.CreateCodeEditor("text", "");
        _phpstan = Ui.Button("PHPStan", async () => await RunPhpStan(), primary: true);
        _phpstan.ToolTip = "Static analysis (phpstan analyse)";
        _cs = Ui.Button("Check style", async () => await RunCsFixer(fix: false));
        _cs.ToolTip = "php-cs-fixer fix --dry-run: lists the files that do not follow the coding style";
        _fix = Ui.Button("Fix style", async () => await RunCsFixer(fix: true));
        _installStan = Ui.Button("Install PHPStan", async () => await Install("phpstan/phpstan"));
        _installCs = Ui.Button("Install PHP-CS-Fixer", async () => await Install("friendsofphp/php-cs-fixer"));

        var bar = Ui.Toolbar(_levelLabel, _level, _paths, _phpstan, _installStan, Ui.Separator(), _cs, _fix, _installCs, Ui.Separator(),
            Ui.ToolButton("", "Stop", () => _proc.Stop()), _status);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        grid.Children.Add(_tree);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        _right.Items.Add(new TabItem { Header = "Detail", Content = _detail.Element, Style = Ui.Style("ToolTabItem") });
        _right.Items.Add(new TabItem { Header = "Output", Content = _output, Style = Ui.Style("ToolTabItem") });
        var rightBorder = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = _right }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        Grid.SetColumn(rightBorder, 2);
        grid.Children.Add(rightBorder);
        Children.Add(grid);

        _tree.SelectedItemChanged += (_, _) => ShowSelected();
        _tree.MouseDoubleClick += (_, e) => { if (OpenSelected()) e.Handled = true; };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenSelected(); };
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(Refresh);
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => _proc.Stop();
    }

    private void Refresh()
    {
        _tree.Items.Clear();
        _detail.Text = "";
        var p = _ctx.Ide.Project;
        var root = p?.Root ?? "";
        var hasStan = p != null && CodeQualityPlugin.PhpStanScript(root) != null;
        var hasCs = p != null && CodeQualityPlugin.CsFixerScript(root) != null;
        var config = p == null ? null : CodeQualityPlugin.PhpStanConfig(root);
        _level.Items.Clear();
        if (config != null) _level.Items.Add(new ComboBoxItem { Content = "from config", Tag = "" });
        for (var i = 0; i <= 10; i++) _level.Items.Add(new ComboBoxItem { Content = i == 10 ? "10 (max)" : i.ToString(), Tag = i.ToString() });
        var saved = _ctx.Settings.Get<string>("level");
        _level.SelectedItem = _level.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == saved) ?? (config != null ? _level.Items[0] : _level.Items[6]);
        _paths.Visibility = config == null && (hasStan || hasCs) ? Visibility.Visible : Visibility.Collapsed;
        _levelLabel.Visibility = hasStan ? Visibility.Visible : Visibility.Collapsed;
        _phpstan.Visibility = hasStan ? Visibility.Visible : Visibility.Collapsed;
        _level.Visibility = hasStan ? Visibility.Visible : Visibility.Collapsed;
        _installStan.Visibility = hasStan || p == null ? Visibility.Collapsed : Visibility.Visible;
        _cs.Visibility = _fix.Visibility = hasCs ? Visibility.Visible : Visibility.Collapsed;
        _installCs.Visibility = hasCs || p == null ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = p == null ? "No project" : !hasStan && !hasCs ? "  PHPStan and PHP-CS-Fixer are not installed in this project (composer require --dev)." : "";
    }

    private async Task<(int code, string output)> Php(string script, string args)
    {
        var p = _ctx.Ide.Project!;
        _output.Reset($"$ php {Path.GetRelativePath(p.Root, script)} {args}");
        _right.SelectedIndex = 1;
        return await _proc.RunAsync(Proc.Php(p), $"{Proc.Quote(script)} {args}", p.Root, _output.Append);
    }

    public async void RunPhpStanFromMenu() => await RunPhpStan();

    private async Task RunPhpStan()
    {
        if (_proc.IsRunning || _ctx.Ide.Project is not { } p || CodeQualityPlugin.PhpStanScript(p.Root) is not { } script) return;
        var level = (_level.SelectedItem as ComboBoxItem)?.Tag as string ?? "5";
        _ctx.Settings.Set("level", level);
        var args = "analyse --error-format=json --no-progress --no-interaction --memory-limit=1G";
        if (CodeQualityPlugin.PhpStanConfig(p.Root) == null) args += " " + string.Join(" ", _paths.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Proc.Quote));
        if (level.Length > 0) args += $" --level={level}";
        _status.Text = "  PHPStan is analysing…";
        var (code, output) = await Php(script, args);
        var (problems, general, parsed) = CodeQualityPlugin.ParsePhpStan(output);
        _tree.Items.Clear();
        _fixes = new();
        if (!parsed) { _status.Text = $"  PHPStan failed (exit {code}): see Output"; return; }
        foreach (var g in general) _tree.Items.Add(new TreeViewItem { Header = Ui.Text("⚠ " + g, size: 12), Tag = new Problem("", 0, g, null, null) });
        foreach (var group in problems.GroupBy(x => x.File).OrderBy(x => x.Key))
            _tree.Items.Add(FileNode(group.Key, group.Count(), group.Select(ProblemNode)));
        _right.SelectedIndex = 0;
        var total = problems.Count + general.Count;
        _status.Text = total == 0 ? $"  PHPStan level {(level.Length > 0 ? level : "config")}: no error ✓" : $"  PHPStan: {total} error(s) in {problems.Select(x => x.File).Distinct().Count()} file(s)";
        _ctx.Ide.Notify(total == 0 ? "PHPStan: no error" : $"PHPStan: {total} error(s)", total == 0 ? NotificationKind.Success : NotificationKind.Warning);
    }

    private TreeViewItem FileNode(string file, int count, IEnumerable<TreeViewItem> children)
    {
        var root = _ctx.Ide.Project?.Root ?? "";
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Ui.Text(Path.GetFileName(file)));
        var rel = Path.GetDirectoryName(Path.GetRelativePath(root, file)) ?? "";
        header.Children.Add(Ui.Text($"  {rel}  {count}", secondary: true, size: 11.5));
        var node = new TreeViewItem { Header = header, IsExpanded = true, ToolTip = file, Tag = file };
        foreach (var c in children) node.Items.Add(c);
        return node;
    }

    private static TreeViewItem ProblemNode(Problem p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Text($"{p.Line,4}  ", secondary: true, size: 12));
        var msg = Ui.Text(p.Message, size: 12);
        msg.MaxWidth = 900;
        row.Children.Add(msg);
        return new TreeViewItem { Header = row, Tag = p, ToolTip = p.Identifier };
    }

    private async Task RunCsFixer(bool fix)
    {
        if (_proc.IsRunning || _ctx.Ide.Project is not { } p || CodeQualityPlugin.CsFixerScript(p.Root) is not { } script) return;
        var config = CodeQualityPlugin.CsFixerConfig(p.Root);
        if (fix && !_ctx.Ide.Confirm("PHP-CS-Fixer", $"Rewrite the files that do not follow the coding style{(config == null ? " (rules @PSR12, no .php-cs-fixer.php)" : "")}? Commit your work first to review the changes in Git.", "Fix"))
            return;
        var args = $"fix {(fix ? "" : "--dry-run --diff ")}--format=json --using-cache=no --no-interaction";
        if (config == null) args += " --rules=@PSR12 " + string.Join(" ", _paths.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Proc.Quote));
        _status.Text = fix ? "  Fixing the coding style…" : "  Checking the coding style…";
        var (code, output) = await Php(script, args);
        var (fixes, parsed) = CodeQualityPlugin.ParseCsFixer(output, p.Root);
        if (!parsed) { _status.Text = $"  PHP-CS-Fixer failed (exit {code}): see Output"; return; }
        _fixes = fixes;
        _tree.Items.Clear();
        foreach (var f in fixes.OrderBy(f => f.File))
        {
            var fixerNodes = f.Fixers.Select(x => new TreeViewItem { Header = Ui.Text(x, secondary: true, size: 12), Tag = f });
            var node = FileNode(f.File, f.Fixers.Count, fixerNodes);
            node.Tag = f;
            node.IsExpanded = false;
            _tree.Items.Add(node);
        }
        _right.SelectedIndex = 0;
        _status.Text = fixes.Count == 0 ? "  Coding style: every file is clean ✓"
            : fix ? $"  {fixes.Count} file(s) fixed" : $"  {fixes.Count} file(s) to fix — select one for the diff, then Fix style";
        if (fixes.Count > 0 && !fix && _tree.Items.Count > 0) ((TreeViewItem)_tree.Items[0]).IsSelected = true;
    }

    private async Task Install(string package)
    {
        if (_proc.IsRunning || _ctx.Ide.Project is not { } p) return;
        var phar = Proc.ComposerPhar(p.Root);
        if (phar == null) { _ctx.Ide.Notify("composer.phar not found (https://getcomposer.org).", NotificationKind.Error); return; }
        if (!_ctx.Ide.Confirm("Install", $"Add {package} to the dev dependencies of the project (composer require --dev {package})?", "Install")) return;
        _output.Reset($"$ composer require --dev {package}");
        _right.SelectedIndex = 1;
        _status.Text = $"  Installing {package}…";
        var (code, _) = await _proc.RunAsync(Proc.Php(p), $"{Proc.Quote(phar)} require --dev {package} --no-interaction", p.Root, _output.Append);
        _ctx.Ide.Notify(code == 0 ? $"{package} installed" : $"composer require {package} failed", code == 0 ? NotificationKind.Success : NotificationKind.Error);
        Refresh();
    }

    private void ShowSelected()
    {
        switch ((_tree.SelectedItem as TreeViewItem)?.Tag)
        {
            case Problem pr:
                _detail.Text = $"{pr.Message}\n\n{(pr.File.Length > 0 ? $"{pr.File}:{pr.Line}\n" : "")}{(pr.Identifier != null ? $"identifier: {pr.Identifier}\n" : "")}{(pr.Tip != null ? $"tip: {pr.Tip}\n" : "")}";
                break;
            case StyleFix f:
                _detail.Text = $"// {f.File}\n// fixers: {string.Join(", ", f.Fixers)}\n\n{(f.Diff.Length > 0 ? f.Diff : "(fixed)")}";
                break;
        }
    }

    private bool OpenSelected()
    {
        switch ((_tree.SelectedItem as TreeViewItem)?.Tag)
        {
            case Problem { File.Length: > 0 } pr: _ctx.Ide.OpenFile(pr.File, pr.Line); return true;
            case StyleFix f: _ctx.Ide.OpenFile(f.File); return true;
            case string file: _ctx.Ide.OpenFile(file); return true;
        }
        return false;
    }
}
