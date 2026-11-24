using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Plugins.Shared;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.NeoAi;

public sealed record Connection(string Id, bool IsDefault, string Provider, string Model, string Server, string Status);

public sealed class NeoAiPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Ide.AddToolWindow(new ToolWindow
    {
        Id = "neoai", Title = "NeoAI", Icon = "", Location = ToolWindowLocation.Left,
        Create = () => new NeoAiView(context),
    });

    /// <summary>Connections of "ai:test --no-ping": | default (default) | ollama | qwen2.5-coder:7b | local | ok |</summary>
    public static List<Connection> ParseConnections(string output) => ConsoleTable.Parse(output).Select(r =>
    {
        var name = r.GetValueOrDefault("Connection") ?? "";
        var isDefault = name.Contains("(default)");
        return new Connection(name.Replace("(default)", "").Trim(), isDefault, r.GetValueOrDefault("Provider") ?? "", r.GetValueOrDefault("Model") ?? "",
            r.GetValueOrDefault("Server") ?? "", r.GetValueOrDefault("Status") ?? "");
    }).Where(c => c.Id.Length > 0).ToList();

    public static string NeoScript(string root) => File.Exists(Path.Combine(root, "bin", "neo")) ? "bin/neo" : "vendor/bin/neo";

    /// <summary>Lines of the CLI that are not part of the answer.</summary>
    public static bool IsNoise(string line) =>
        line.TrimStart().StartsWith("Data is sent to") || Regex.IsMatch(line, @"^\s*\.\.\.\s.*is thinking") || line.Trim() is "Secrets are redacted before sending.";
}

public sealed class NeoAiView : DockPanel
{
    private readonly IPluginContext _ctx;
    private readonly ComboBox _connections = new() { MinWidth = 160, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _connInfo = Ui.Text("", secondary: true, size: 11.5);
    private readonly StackPanel _messages = new() { Margin = new Thickness(8, 6, 8, 6) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly Button _send, _stop;
    private readonly Proc _chatProc = new(), _scanProc = new();
    private readonly List<(string q, string a)> _history = new();
    private readonly ComboBox _focus = new() { Width = 130, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _scanPath = Ui.TextBox(width: 150, tooltip: "Path to audit, e.g. src/Controller (empty = scan.paths of neo_ai.yaml)");
    private readonly OutputPane _scanOutput = new();
    private readonly ListBox _reports = new() { MaxHeight = 170 };
    private bool _loaded;

    public NeoAiView(IPluginContext ctx)
    {
        _ctx = ctx;
        _send = Ui.Button("Send", async () => await Ask(), primary: true);
        _send.ToolTip = "Enter (Shift+Enter: new line)";
        _stop = Ui.Button("Stop", () => _chatProc.Stop());
        _stop.IsEnabled = false;

        var head = new StackPanel { Margin = new Thickness(8, 6, 8, 4) };
        var connRow = new DockPanel();
        var refresh = Ui.ToolButton("", "Reload the connections (ai:test)", async () => await LoadConnections());
        var config = Ui.ToolButton("", "Open config/packages/neo_ai.yaml", () => { if (ctx.Ide.Project is { } p) ctx.Ide.OpenFile(Path.Combine(p.Root, "config", "packages", "neo_ai.yaml")); });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(refresh);
        right.Children.Add(config);
        SetDock(right, Dock.Right);
        connRow.Children.Add(right);
        connRow.Children.Add(_connections);
        head.Children.Add(connRow);
        _connInfo.Margin = new Thickness(2, 4, 0, 0);
        _connInfo.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(_connInfo);
        var headBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = head }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        SetDock(headBorder, Dock.Top);
        Children.Add(headBorder);
        _connections.SelectionChanged += (_, _) => ShowConnection();

        var tabs = new TabControl { Style = Ui.Style("ToolTabControl"), BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Chat", Content = BuildChat(), Style = Ui.Style("ToolTabItem") });
        tabs.Items.Add(new TabItem { Header = "Audit", Content = BuildAudit(), Style = Ui.Style("ToolTabItem") });
        Children.Add(tabs);

        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { _history.Clear(); _messages.Children.Clear(); Welcome(); if (_loaded) { await LoadConnections(); LoadReports(); } });
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadConnections(); LoadReports(); Welcome(); } };
        Unloaded += (_, _) => { _chatProc.Stop(); _scanProc.Stop(); };
    }

    // ------------------------------------------------------------------ connections

    private Connection? Selected => (_connections.SelectedItem as ComboBoxItem)?.Tag as Connection;

    private async Task LoadConnections()
    {
        if (_ctx.Ide.Project == null) return;
        _connInfo.Text = "Reading the NeoAI configuration…";
        var r = await _ctx.Ide.RunNeoAsync("ai:test --no-ping");
        var list = NeoAiPlugin.ParseConnections(r.Output);
        _connections.Items.Clear();
        foreach (var c in list) _connections.Items.Add(new ComboBoxItem { Content = c.IsDefault ? $"{c.Id} (default)" : c.Id, Tag = c });
        var saved = _ctx.Settings.Get<string>("connection");
        _connections.SelectedItem = _connections.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((Connection)i.Tag).Id == saved)
                                    ?? _connections.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((Connection)i.Tag).IsDefault)
                                    ?? _connections.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (list.Count == 0)
            _connInfo.Text = r.Output.Contains("disabled", StringComparison.OrdinalIgnoreCase) || !r.Success
                ? "NeoAI is not available: it works when APP_DEBUG=1 (see config/packages/neo_ai.yaml)."
                : "No NeoAI connection configured.";
    }

    private void ShowConnection()
    {
        if (Selected is not { } c) return;
        _ctx.Settings.Set("connection", c.Id);
        _connInfo.Text = $"{c.Provider} · {c.Model} · {(c.Server == "local" ? "runs on this machine" : "remote: code is sent to the provider (secrets redacted)")}"
                         + (c.Status is "ok" or "" ? "" : $" · {c.Status}");
    }

    private string ConnectionArg => Selected is { IsDefault: false } c ? $" --connection={Proc.Quote(c.Id)}" : "";

    // ------------------------------------------------------------------ chat

    private FrameworkElement BuildChat()
    {
        var dock = new DockPanel();
        var inputArea = new DockPanel { Margin = new Thickness(8, 6, 8, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Ui.ToolButton("", "New conversation", () => { _chatProc.Stop(); _history.Clear(); _messages.Children.Clear(); Welcome(); }));
        buttons.Children.Add(_stop);
        _send.Margin = new Thickness(0);
        buttons.Children.Add(_send);
        DockPanel.SetDock(buttons, Dock.Bottom);
        inputArea.Children.Add(buttons);
        inputArea.Children.Add(_input);
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await Ask(); }
        };
        var inputBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = inputArea }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        DockPanel.SetDock(inputBorder, Dock.Bottom);
        dock.Children.Add(inputBorder);
        _scroll.Content = _messages;
        dock.Children.Add(_scroll);
        return dock;
    }

    private void Welcome()
    {
        if (_messages.Children.Count > 0) return;
        _messages.Children.Add(Ui.Empty("Ask NeoAI about your project:\n“Where are the routes of the blog defined?”\n“Write a repository method to find the published posts.”"));
    }

    private TextBox Bubble(string text, bool user)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7),
            FontFamily = user ? SystemFonts.MessageFontFamily : Ui.Mono, FontSize = user ? 13 : 12.5,
        };
        box.Themed(BackgroundProperty, user ? "SelectedInactiveBg" : "PanelBg");
        box.Themed(Control.ForegroundProperty, "Fg");
        var border = new Border
        {
            Child = box, CornerRadius = new CornerRadius(8), Margin = new Thickness(user ? 30 : 0, 4, user ? 0 : 20, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, BorderThickness = new Thickness(1), ClipToBounds = true,
        };
        border.Themed(Border.BorderBrushProperty, "SubtleBorder");
        border.Themed(Border.BackgroundProperty, user ? "SelectedInactiveBg" : "PanelBg");
        _messages.Children.Add(border);
        return box;
    }

    private async Task Ask()
    {
        var question = _input.Text.Trim();
        if (question.Length == 0 || _chatProc.IsRunning || _ctx.Ide.Project is not { } p) return;
        if (_messages.Children.Count == 1 && _messages.Children[0] is TextBlock) _messages.Children.Clear();
        _input.Text = "";
        Bubble(question, user: true);
        var answer = Bubble("…", user: false);
        _scroll.ScrollToEnd();
        _send.IsEnabled = false;
        _stop.IsEnabled = true;

        // ai:start --ask answers one question: the last exchanges are sent with it so that the conversation keeps its context
        var prompt = new StringBuilder();
        if (_history.Count > 0)
        {
            prompt.AppendLine("Previous messages of this conversation:");
            foreach (var (q, a) in _history.TakeLast(3)) prompt.AppendLine($"User: {q}").AppendLine($"Assistant: {(a.Length > 1500 ? a[..1500] + "…" : a)}");
            prompt.AppendLine().Append("New question: ");
        }
        prompt.Append(question);
        var text = prompt.ToString();
        if (text.Length > 24_000) text = text[^24_000..];

        var sb = new StringBuilder();
        var error = false;
        void OnLine(string line)
        {
            if (NeoAiPlugin.IsNoise(line)) return;
            if (line.Contains("[ERROR]")) error = true;
            lock (sb) sb.AppendLine(line.StartsWith(' ') && error ? line.Trim() : line);
            string snapshot;
            lock (sb) snapshot = sb.ToString().Trim();
            Dispatcher.BeginInvoke(() => { answer.Text = snapshot; _scroll.ScrollToEnd(); });
        }
        var (code, _) = await _chatProc.RunAsync(Proc.Php(p), $"{NeoAiPlugin.NeoScript(p.Root)} ai:start --ask={Proc.Quote(text)}{ConnectionArg} --no-ansi --no-interaction", p.Root, OnLine);
        var result = sb.ToString().Trim();
        if (result.Length == 0) result = code == 0 ? "(empty answer)" : "Stopped.";
        if (error || code != 0)
        {
            answer.Themed(Control.ForegroundProperty, "ErrorFg");
            if (result.Contains("Network error") && Selected?.Server == "local")
                result += "\n\nStart Ollama (ollama serve) or choose a remote connection; API keys go in .env.local (NEO_AI_API_KEY, OPENAI_API_KEY…).";
        }
        else _history.Add((question, result));
        answer.Text = result;
        _send.IsEnabled = true;
        _stop.IsEnabled = false;
        _input.Focus();
    }

    // ------------------------------------------------------------------ audit

    private FrameworkElement BuildAudit()
    {
        foreach (var f in new[] { "all", "security", "performance", "bugs", "conventions" }) _focus.Items.Add(f);
        _focus.SelectedIndex = 0;
        var dock = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 4) };
        bar.Children.Add(Ui.Text("Focus ", secondary: true));
        bar.Children.Add(_focus);
        bar.Children.Add(_scanPath);
        var run = Ui.Button("Run audit", async () => await Scan(dryRun: false), primary: true);
        var dry = Ui.Button("Files only", async () => await Scan(dryRun: true));
        dry.ToolTip = "List the files that would be sent (--dry-run), nothing is sent";
        var stop = Ui.ToolButton("", "Stop", () => _scanProc.Stop());
        bar.Children.Add(run);
        bar.Children.Add(dry);
        bar.Children.Add(stop);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        var reportsHead = new DockPanel { Margin = new Thickness(10, 4, 8, 2) };
        var reload = Ui.ToolButton("", "Reload the reports", LoadReports);
        DockPanel.SetDock(reload, Dock.Right);
        reportsHead.Children.Add(reload);
        reportsHead.Children.Add(Ui.Title("Reports (var/ai/reports)"));
        DockPanel.SetDock(reportsHead, Dock.Top);
        dock.Children.Add(reportsHead);
        DockPanel.SetDock(_reports, Dock.Top);
        dock.Children.Add(_reports);
        _reports.MouseDoubleClick += (_, _) => { if ((_reports.SelectedItem as ListBoxItem)?.Tag is string f) _ctx.Ide.OpenFile(f); };
        var outBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _scanOutput, Margin = new Thickness(0, 6, 0, 0) }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        dock.Children.Add(outBorder);
        return dock;
    }

    private void LoadReports()
    {
        _reports.Items.Clear();
        if (_ctx.Ide.Project is not { } p) return;
        var dir = Path.Combine(p.Root, "var", "ai", "reports");
        var files = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.md").OrderByDescending(f => f.LastWriteTimeUtc).Take(30).ToList() : new();
        foreach (var f in files)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Icon("", 12, "Accent"));
            row.Children.Add(new TextBlock { Width = 6 });
            row.Children.Add(Ui.Text(f.Name, size: 12));
            row.Children.Add(Ui.Text($"  {f.LastWriteTime:dd/MM HH:mm}", secondary: true, size: 11.5));
            _reports.Items.Add(new ListBoxItem { Content = row, Tag = f.FullName, ToolTip = "Double-click to open" });
        }
        if (files.Count == 0) _reports.Items.Add(new ListBoxItem { Content = Ui.Text("No report yet.", secondary: true, size: 12), IsEnabled = false });
    }

    private async Task Scan(bool dryRun)
    {
        if (_scanProc.IsRunning || _ctx.Ide.Project is not { } p) return;
        if (!dryRun && Selected is { Server: "remote" } c &&
            !_ctx.Ide.Confirm("NeoAI audit", $"The code of the project is sent to {c.Provider} ({c.Model}). Secrets are redacted before sending. Continue?", "Run"))
            return;
        var args = $"{NeoAiPlugin.NeoScript(p.Root)} ai:scan --focus={_focus.SelectedItem}{ConnectionArg}";
        if (_scanPath.Text.Trim().Length > 0) args += $" --path={Proc.Quote(_scanPath.Text.Trim())}";
        if (dryRun) args += " --dry-run";
        args += " --no-ansi --no-interaction";
        _scanOutput.Reset("$ php " + args.Replace(" --no-ansi --no-interaction", ""));
        var (code, output) = await _scanProc.RunAsync(Proc.Php(p), args, p.Root, _scanOutput.Append);
        LoadReports();
        var report = Regex.Match(output, @"(var[/\\]ai[/\\]reports[/\\][\w\-.]+\.md)");
        if (code == 0 && report.Success) _ctx.Ide.OpenFile(Path.Combine(p.Root, report.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
        _ctx.Ide.Notify(code == 0 ? (dryRun ? "NeoAI: files listed" : "NeoAI audit finished") : "NeoAI audit failed (see the output)", code == 0 ? NotificationKind.Success : NotificationKind.Error);
    }
}
