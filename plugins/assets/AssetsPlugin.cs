using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NeoIDE.Plugins.Shared;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Assets;

public sealed class AssetsPlugin : IPlugin
{
    internal static AssetsView? View;

    public void Activate(IPluginContext context)
    {
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "assets", Title = "Assets", Icon = "", Location = ToolWindowLocation.Bottom,
            Create = () => View = new AssetsView(context),
        });
        context.Ide.AddMenuItem("Tailwind: start / stop watching", () =>
        {
            context.Ide.ShowToolWindow("assets");
            View?.ToggleWatch();
        }, "");
    }

    public void Deactivate() => View?.StopAll();

    public static string NeoScript(string root) => File.Exists(System.IO.Path.Combine(root, "bin", "neo")) ? "bin/neo" : "vendor/bin/neo";

    /// <summary>config/packages/tailwind.yaml exists (tailwind:install was run).</summary>
    public static bool TailwindInstalled(string root) =>
        File.Exists(System.IO.Path.Combine(root, "config", "packages", "tailwind.yaml")) || Directory.Exists(System.IO.Path.Combine(root, "var", "tailwind"));
}

public sealed class AssetsView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly OutputPane _output = new();
    private readonly Proc _watch = new(), _task = new();
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.Gray };
    private readonly TextBlock _state = Ui.Text("", secondary: true);
    private readonly Button _watchButton, _install;
    private readonly ListBox _files = new();
    private readonly TextBlock _filesInfo = Ui.Text("", secondary: true, size: 11.5);
    private readonly CheckBox _minify = new() { Content = "Minify", Margin = new Thickness(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };

    public AssetsView(IPluginContext ctx)
    {
        _ctx = ctx;
        _watchButton = Ui.Button("Watch", ToggleWatch, primary: true);
        _watchButton.ToolTip = "tailwind:run --watch: recompiles on every change of the templates or the CSS";
        _install = Ui.Button("Install Tailwind", async () => await Install());

        var bar = Ui.Toolbar(_dot, Ui.Text("Tailwind ", secondary: true), _state, Ui.Separator(),
            _install, _watchButton,
            Ui.Button("Build", async () => await RunTask("tailwind:run" + (_minify.IsChecked == true ? " --minify" : ""))),
            _minify, Ui.Separator(),
            Ui.Button("Reload assets", async () => await RunTask("asset:reload" + (_minify.IsChecked == true ? " --minify" : ""))),
            Ui.ToolButton("", "Clear the output", () => _output.Reset()),
            Ui.ToolButton("", "Open config/packages/tailwind.yaml", () => OpenConfig()));
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 200 });
        grid.Children.Add(_output);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        var filesPanel = new DockPanel();
        var filesHead = new DockPanel { Margin = new Thickness(10, 6, 6, 4) };
        var reload = Ui.ToolButton("", "Reload the list", LoadFiles);
        SetDock(reload, Dock.Right);
        filesHead.Children.Add(reload);
        var title = Ui.Title("public/builds");
        SetDock(title, Dock.Left);
        filesHead.Children.Add(title);
        filesHead.Children.Add(_filesInfo);
        SetDock(filesHead, Dock.Top);
        filesPanel.Children.Add(filesHead);
        filesPanel.Children.Add(_files);
        _files.MouseDoubleClick += (_, _) => { if ((_files.SelectedItem as ListBoxItem)?.Tag is string f) _ctx.Ide.OpenFile(f); };
        var filesBorder = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = filesPanel }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        Grid.SetColumn(filesBorder, 2);
        grid.Children.Add(filesBorder);
        Children.Add(grid);

        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(() => { StopAll(); Refresh(); });
        ctx.Ide.ProjectClosed += StopAll;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var p = _ctx.Ide.Project;
        var installed = p != null && AssetsPlugin.TailwindInstalled(p.Root);
        _install.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        _watchButton.IsEnabled = installed;
        _watchButton.Content = _watch.IsRunning ? "Stop watching" : "Watch";
        _dot.Fill = new SolidColorBrush(_watch.IsRunning ? Color.FromRgb(0x5F, 0xB8, 0x65) : installed ? Color.FromRgb(0x86, 0x8A, 0x91) : Color.FromRgb(0xE8, 0xA3, 0x3D));
        _state.Text = p == null ? "no project" : _watch.IsRunning ? "watching" : installed ? "installed" : "not installed (no Node.js needed)";
        LoadFiles();
    }

    private void OpenConfig()
    {
        if (_ctx.Ide.Project is not { } p) return;
        var f = System.IO.Path.Combine(p.Root, "config", "packages", "tailwind.yaml");
        if (File.Exists(f)) _ctx.Ide.OpenFile(f);
        else _ctx.Ide.Notify("Tailwind is not installed yet (Install Tailwind).", NotificationKind.Info);
    }

    private async Task Install()
    {
        if (_ctx.Ide.Project is null) return;
        if (!_ctx.Ide.Confirm("Install Tailwind CSS", "Download the Tailwind CSS standalone CLI from GitHub into var/tailwind/ and prepare assets/css/app.css (tailwind:install)?", "Install")) return;
        await RunTask("tailwind:install");
        Refresh();
    }

    public void ToggleWatch()
    {
        if (_watch.IsRunning) { _watch.Stop(); return; }
        if (_ctx.Ide.Project is not { } p) return;
        _output.Append($"$ php {AssetsPlugin.NeoScript(p.Root)} tailwind:run --watch");
        var task = _watch.RunAsync(Proc.Php(p), $"{AssetsPlugin.NeoScript(p.Root)} tailwind:run --watch --no-ansi --no-interaction", p.Root, line =>
        {
            _output.Append(line);
            if (Regex.IsMatch(line, @"\b(Done|rebuilt|compiled)\b", RegexOptions.IgnoreCase)) Dispatcher.BeginInvoke(LoadFiles);
            if (Regex.IsMatch(line, @"\berror\b", RegexOptions.IgnoreCase)) Dispatcher.BeginInvoke(() => _ctx.Ide.Notify("Tailwind: " + line.Trim(), NotificationKind.Error));
        });
        Refresh();
        _ = task.ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _output.Append($"[watch stopped, exit {t.Result.code}]");
            Refresh();
        }));
    }

    private async Task RunTask(string command)
    {
        if (_ctx.Ide.Project is not { } p || _task.IsRunning) return;
        _output.Append($"$ php {AssetsPlugin.NeoScript(p.Root)} {command}");
        var (code, _) = await _task.RunAsync(Proc.Php(p), $"{AssetsPlugin.NeoScript(p.Root)} {command} --no-ansi --no-interaction", p.Root, _output.Append);
        _output.Append($"[exit {code}]");
        _ctx.Ide.Notify($"{command}: {(code == 0 ? "done" : "failed")}", code == 0 ? NotificationKind.Success : NotificationKind.Error);
        LoadFiles();
    }

    private void LoadFiles()
    {
        _files.Items.Clear();
        if (_ctx.Ide.Project is not { } p) return;
        var dir = System.IO.Path.Combine(p.Root, "public", "builds");
        if (!Directory.Exists(dir)) { _filesInfo.Text = "  not compiled yet"; return; }
        var files = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).Where(f => f.Extension is ".css" or ".js" or ".map" or ".json")
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(200).ToList();
        foreach (var f in files)
        {
            var row = new DockPanel();
            var size = Ui.Text(Size(f.Length), secondary: true, size: 11.5);
            SetDock(size, Dock.Right);
            row.Children.Add(size);
            var color = f.Extension == ".css" ? "#4C9BE8" : f.Extension == ".js" ? "#E8C63D" : "#868A91";
            row.Children.Add(Ui.Badge(f.Extension.TrimStart('.'), Ui.Hex(color), 34));
            row.Children.Add(Ui.Text(System.IO.Path.GetRelativePath(dir, f.FullName), size: 12));
            _files.Items.Add(new ListBoxItem { Content = row, Tag = f.FullName, ToolTip = $"{f.FullName}\n{f.LastWriteTime:dd/MM HH:mm:ss}" });
        }
        _filesInfo.Text = $"  {files.Count} file(s) · {Size(files.Sum(f => f.Length))}";
    }

    private static string Size(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" : $"{b / 1024.0 / 1024.0:0.0} MB";

    public void StopAll()
    {
        _watch.Stop();
        _task.Stop();
    }
}
