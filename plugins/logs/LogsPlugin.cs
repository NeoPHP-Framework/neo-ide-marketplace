using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Logs;

public sealed class LogsPlugin : IPlugin
{
    public void Activate(IPluginContext context) => context.Ide.AddToolWindow(new ToolWindow
    {
        Id = "logs", Title = "Logs", Icon = "", Location = ToolWindowLocation.Bottom,
        Create = () => new LogsView(context),
    });
}

/// <summary>Bottom tool window: entries of a var/log file, followed live.</summary>
public sealed class LogsView : DockPanel
{
    private const long InitialTail = 2 * 1024 * 1024;
    private const int MaxEntries = 20_000;
    private readonly IPluginContext _ctx;
    private readonly ComboBox _files = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Dictionary<string, ToggleButton> _levelToggles = new();
    private readonly TextBox _search = Ui.TextBox(width: 220, tooltip: "Search in the messages and contexts");
    private readonly ToggleButton _follow = new() { Content = "Follow", IsChecked = true, Style = Ui.Style("ModeToggle"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Scroll to the new entries" };
    private readonly TextBlock _counts = Ui.Text("", secondary: true);
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly ICollectionView _view;
    private readonly ListBox _list = new();
    private readonly ICodeEditor _detail;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _file;
    private long _position;
    private string _partial = "";

    public LogsView(IPluginContext ctx)
    {
        _ctx = ctx;
        _detail = ctx.Ide.CreateCodeEditor("json", "");
        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = Matches;

        var levels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (label, level) in new[] { ("DEBUG", "DEBUG"), ("INFO", "INFO"), ("NOTICE", "NOTICE"), ("WARN", "WARNING"), ("ERROR", "ERROR+") })
        {
            var t = new ToggleButton
            {
                Content = new TextBlock { Text = label, Foreground = new SolidColorBrush(Ui.Hex(Logs.Color(level.TrimEnd('+')))), FontWeight = FontWeights.SemiBold, FontSize = 11.5 },
                IsChecked = true, Style = Ui.Style("ModeToggle"), Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(7, 2, 7, 2), ToolTip = $"Show the {level.TrimEnd('+')} entries",
            };
            t.Click += (_, _) => _view.Refresh();
            _levelToggles[level] = t;
            levels.Children.Add(t);
        }
        _search.TextChanged += (_, _) => _view.Refresh();
        _files.SelectionChanged += (_, _) => { if (_files.SelectedItem is ComboBoxItem { Tag: string f }) Open(f); };

        var bar = Ui.Toolbar(_files, levels, _search, _follow,
            Ui.Separator(),
            Ui.ToolButton("", "Reload the file list", ReloadFiles),
            Ui.ToolButton("", "Open the file in the editor", () => { if (_file != null) ctx.Ide.OpenFile(_file); }),
            Ui.ToolButton("", "Clear the log file", ClearFile),
            _counts);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420), MinWidth = 200 });
        _list.ItemsSource = _view;
        _list.ItemTemplate = Template();
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.SelectionChanged += (_, _) => ShowDetail(_list.SelectedItem as LogEntry);
        grid.Children.Add(_list);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        var detail = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = _detail.Element }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);
        Children.Add(grid);

        _timer.Tick += (_, _) => Poll();
        Loaded += (_, _) => { if (_file == null) ReloadFiles(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(ReloadFiles);
    }

    private string? LogDir => _ctx.Ide.Project is { } p ? Path.Combine(p.Root, "var", "log") : null;

    private void ReloadFiles()
    {
        var current = _file;
        _files.Items.Clear();
        if (LogDir is not { } dir || !Directory.Exists(dir))
        {
            _entries.Clear();
            _file = null;
            _counts.Text = "  no var/log directory yet";
            return;
        }
        var files = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.Name is "app.log" ? 2 : f.Name is "framework.log" ? 1 : 0).ThenByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var f in files)
            _files.Items.Add(new ComboBoxItem { Content = $"{f.Name}  ({Size(f.Length)})", Tag = f.FullName });
        _files.SelectedItem = _files.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? _files.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (files.Count == 0) { _entries.Clear(); _counts.Text = "  no log file yet"; }
    }

    private static string Size(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0} KB" : $"{b / 1024.0 / 1024.0:0.0} MB";

    private void Open(string file)
    {
        _file = file;
        _entries.Clear();
        _position = 0;
        _partial = "";
        try
        {
            var length = new FileInfo(file).Length;
            _position = Math.Max(0, length - InitialTail);
            Read(skipFirstLine: _position > 0);
        }
        catch (Exception ex) { _counts.Text = "  " + ex.Message; }
        ScrollToEnd();
    }

    private void Poll()
    {
        if (_file == null || !File.Exists(_file)) return;
        var length = new FileInfo(_file).Length;
        if (length < _position) { Open(_file); return; } // rotated or cleared
        if (length == _position) return;
        var before = _entries.Count;
        Read(skipFirstLine: false);
        if (_entries.Count != before && _follow.IsChecked == true) ScrollToEnd();
    }

    private void Read(bool skipFirstLine)
    {
        using var fs = new FileStream(_file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(_position, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var text = _partial + reader.ReadToEnd();
        _position = fs.Length;
        var lines = text.Split('\n').ToList();
        _partial = lines[^1];   // incomplete last line: kept for the next read
        lines.RemoveAt(lines.Count - 1);
        if (skipFirstLine && lines.Count > 0) lines.RemoveAt(0);
        var parsed = new List<LogEntry>();
        // a continuation line of the last entry
        if (_entries.Count > 0) parsed.Add(_entries[^1]);
        LogParser.Parse(lines, parsed);
        if (_entries.Count > 0) parsed.RemoveAt(0);
        if (parsed.Count == 0) { UpdateCounts(); return; }
        // bulk load (opening a file): detach the list, otherwise it re-filters at every Add
        var bulk = parsed.Count > 200;
        if (bulk) _list.ItemsSource = null;
        foreach (var e in parsed) _entries.Add(e);
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        if (bulk) _list.ItemsSource = _view;
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var errors = _entries.Count(e => e.Severity >= 4);
        var warnings = _entries.Count(e => e.Severity == 3);
        _counts.Text = $"  {_entries.Count:N0} entries · {errors} error(s) · {warnings} warning(s)";
    }

    private bool Matches(object o)
    {
        if (o is not LogEntry e) return false;
        var key = e.Severity >= 4 ? "ERROR+" : e.Level;
        if (_levelToggles.TryGetValue(key, out var t) && t.IsChecked != true) return false;
        var q = _search.Text.Trim();
        return q.Length == 0 || e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Context?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || e.Channel.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ScrollToEnd()
    {
        if (_list.Items.Count > 0) Dispatcher.BeginInvoke(() => _list.ScrollIntoView(_list.Items[^1]), DispatcherPriority.Background);
    }

    private void ShowDetail(LogEntry? e)
    {
        if (e == null) { _detail.Text = ""; return; }
        _detail.Text = $"// [{e.Time}] {e.Channel}.{e.Level}\n// {e.Message.Replace("\n", "\n// ")}\n" + (e.Context ?? "");
    }

    private void ClearFile()
    {
        if (_file == null) return;
        if (!_ctx.Ide.Confirm("Clear log", $"Empty {Path.GetFileName(_file)}? Its entries are lost.", "Clear")) return;
        try
        {
            File.WriteAllText(_file, "");
            Open(_file);
        }
        catch (Exception ex) { _ctx.Ide.Notify(ex.Message, NotificationKind.Error); }
    }

    private static DataTemplate Template()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        var time = new FrameworkElementFactory(typeof(TextBlock));
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogEntry.Time)));
        time.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        time.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
        time.SetValue(TextBlock.FontFamilyProperty, Ui.Mono);
        time.SetValue(TextBlock.FontSizeProperty, 12.0);
        root.AppendChild(time);
        var level = new FrameworkElementFactory(typeof(TextBlock));
        level.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogEntry.Level)));
        level.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(LogEntry.Level)) { Converter = new LevelBrush() });
        level.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        level.SetValue(TextBlock.WidthProperty, 74.0);
        level.SetValue(TextBlock.FontSizeProperty, 12.0);
        root.AppendChild(level);
        var channel = new FrameworkElementFactory(typeof(TextBlock));
        channel.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogEntry.Channel)));
        channel.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        channel.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
        channel.SetValue(TextBlock.MinWidthProperty, 60.0);
        root.AppendChild(channel);
        var message = new FrameworkElementFactory(typeof(TextBlock));
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogEntry.Message)) { Converter = new FirstLine() });
        message.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        message.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        root.AppendChild(message);
        return new DataTemplate { VisualTree = root };
    }

    private sealed class LevelBrush : IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => new SolidColorBrush(Ui.Hex(Logs.Color(value as string ?? "")));
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => Binding.DoNothing;
    }

    private sealed class FirstLine : IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is string s && s.IndexOf('\n') is var i and >= 0 ? s[..i] + "  …" : value;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => Binding.DoNothing;
    }
}
