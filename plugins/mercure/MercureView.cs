using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Mercure;

/// <summary>Bottom tool window: hub status, live subscription, event detail, publication.</summary>
public sealed class MercureView : DockPanel
{
    private readonly IPluginContext _ctx;
    private MercureConfig _config = new();
    private readonly TextBox _hub = Ui.TextBox(width: 360, tooltip: "Hub URL (MERCURE_PUBLIC_URL / MERCURE_URL of the .env)");
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hubStatus = Ui.Text("", secondary: true);
    private readonly TextBox _topics = Ui.TextBox("*", tooltip: "Topics to subscribe to, separated by spaces or commas (* = every topic)");
    private readonly Button _subscribe;
    private readonly TextBlock _subStatus = Ui.Text("Not subscribed", secondary: true);
    private readonly ObservableCollection<MercureEvent> _events = new();
    private readonly ListBox _list = new();
    private readonly ICodeEditor _detail;
    private readonly TextBox _pubTopic = Ui.TextBox("https://example.com/books/1");
    private readonly ICodeEditor _pubData;
    private readonly CheckBox _private = new() { Content = "Private", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _pubType = Ui.TextBox(width: 110, tooltip: "Event type (optional)");
    private readonly TextBlock _pubResult = Ui.Text("", secondary: true);
    private CancellationTokenSource? _cts;

    public MercureView(IPluginContext ctx)
    {
        _ctx = ctx;
        _detail = ctx.Ide.CreateCodeEditor("json", "");
        _pubData = ctx.Ide.CreateCodeEditor("json", "{\n    \"status\": \"OutOfStock\"\n}");
        _subscribe = Ui.Button("Subscribe", ToggleSubscription, primary: true);

        // ---- header
        _hub.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Ping(); };
        var header = Ui.Toolbar(_dot, Ui.Text("Hub ", secondary: true), _hub,
            Ui.ToolButton("", "Check the hub", async () => await Ping()), _hubStatus,
            Ui.Separator(),
            Ui.ToolButton("", "Copy a subscriber JWT", () => CopyToken(ctx, publish: false)),
            Ui.ToolButton("", "Copy a publisher JWT", () => CopyToken(ctx, publish: true)),
            Ui.ToolButton("", "Open config/packages/mercure.yaml", () => { if (ctx.Ide.Project is { } p) ctx.Ide.OpenFile(System.IO.Path.Combine(p.Root, "config", "packages", "mercure.yaml")); }));
        SetDock(header, Dock.Top);
        Children.Add(header);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 260 });
        Children.Add(grid);

        // ---- subscription
        var sub = new DockPanel();
        var subBar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        var subButtons = new StackPanel { Orientation = Orientation.Horizontal };
        subButtons.Children.Add(_subscribe);
        subButtons.Children.Add(Ui.ToolButton("", "Clear the events", () => { _events.Clear(); _detail.Text = ""; }));
        SetDock(subButtons, Dock.Right);
        subBar.Children.Add(subButtons);
        subBar.Children.Add(_topics);
        SetDock(subBar, Dock.Top);
        sub.Children.Add(subBar);
        var subStatusBox = new Border { Padding = new Thickness(10, 0, 10, 4), Child = _subStatus };
        SetDock(subStatusBox, Dock.Top);
        sub.Children.Add(subStatusBox);
        _list.ItemsSource = _events;
        _list.ItemTemplate = EventTemplate();
        _list.SelectionChanged += (_, _) => ShowDetail(_list.SelectedItem as MercureEvent);
        sub.Children.Add(_list);
        grid.Children.Add(sub);

        grid.Children.Add(Splitter(1));
        var detailHost = new Border { BorderThickness = new Thickness(1, 0, 1, 0), Child = _detail.Element }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        Grid.SetColumn(detailHost, 2);
        grid.Children.Add(detailHost);
        grid.Children.Add(Splitter(3));

        // ---- publication
        var pub = new DockPanel { Margin = new Thickness(10, 6, 10, 8) };
        var pubTitle = Ui.Title("Publish an update");
        SetDock(pubTitle, Dock.Top);
        pub.Children.Add(pubTitle);
        _pubTopic.Margin = new Thickness(0, 6, 0, 6);
        _pubTopic.ToolTip = "Topic(s), separated by spaces";
        SetDock(_pubTopic, Dock.Top);
        pub.Children.Add(_pubTopic);
        var actions = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var publish = Ui.Button("Publish", async () => await Publish(), primary: true);
        publish.Margin = new Thickness(0);
        SetDock(publish, Dock.Right);
        actions.Children.Add(publish);
        var opts = new StackPanel { Orientation = Orientation.Horizontal };
        opts.Children.Add(_private);
        opts.Children.Add(Ui.Text("type ", secondary: true));
        opts.Children.Add(_pubType);
        actions.Children.Add(opts);
        SetDock(actions, Dock.Bottom);
        var resultBox = new Border { Child = _pubResult, Margin = new Thickness(0, 4, 0, 0) };
        _pubResult.TextWrapping = TextWrapping.Wrap;
        SetDock(resultBox, Dock.Bottom);
        pub.Children.Add(resultBox);
        pub.Children.Add(actions);
        var dataBox = new Border { BorderThickness = new Thickness(1), Child = _pubData.Element, MinHeight = 60 }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        pub.Children.Add(dataBox);
        Grid.SetColumn(pub, 4);
        grid.Children.Add(pub);

        ctx.Ide.ProjectOpened += _ => Dispatcher.BeginInvoke(async () => { Stop(); await Reload(); });
        Loaded += async (_, _) => { if (_config.HubUrl.Length == 0) await Reload(); };
    }

    private static GridSplitter Splitter(int column)
    {
        var s = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(s, column);
        return s;
    }

    private static DataTemplate EventTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var time = new FrameworkElementFactory(typeof(TextBlock));
        time.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MercureEvent.Received)) { StringFormat = "HH:mm:ss" });
        time.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        time.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        root.AppendChild(time);
        var type = new FrameworkElementFactory(typeof(TextBlock));
        type.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MercureEvent.Type)) { TargetNullValue = "message" });
        type.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        type.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        root.AppendChild(type);
        var data = new FrameworkElementFactory(typeof(TextBlock));
        data.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MercureEvent.Data)));
        data.SetValue(TextBlock.FontFamilyProperty, Ui.Mono);
        data.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        data.SetValue(TextBlock.MaxWidthProperty, 600.0);
        data.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        root.AppendChild(data);
        return new DataTemplate { VisualTree = root };
    }

    private async Task Reload()
    {
        _config = MercureConfig.FromProject(_ctx.Ide.Project);
        _hub.Text = _config.HubUrl;
        await Ping();
    }

    private async Task Ping()
    {
        _config.HubUrl = _hub.Text.Trim();
        _hubStatus.Text = "checking…";
        _dot.Fill = Brushes.Gray;
        var (ok, message) = await MercureClient.PingAsync(_config.HubUrl);
        _dot.Fill = new SolidColorBrush(ok ? Color.FromRgb(0x5F, 0xB8, 0x65) : Color.FromRgb(0xE5, 0x5B, 0x5B));
        _hubStatus.Text = (ok ? message : "unreachable: " + message) + (_config.Secret.Length == 0 ? " · MERCURE_JWT_SECRET is empty" : $" · {_config.Algorithm}");
        _hubStatus.ToolTip = ok ? null : "Start the hub (FrankenPHP or the Mercure binary) and check MERCURE_URL.";
    }

    private string[] Topics(string text) => text.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    private async void ToggleSubscription()
    {
        if (_cts != null) { Stop(); return; }
        var topics = Topics(_topics.Text);
        if (topics.Length == 0) { _subStatus.Text = "Enter at least one topic (* = every topic)."; return; }
        _config.HubUrl = _hub.Text.Trim();
        _cts = new CancellationTokenSource();
        var cts = _cts;
        _subscribe.Content = "Stop";
        _subStatus.Text = "Connecting…";
        try
        {
            await Task.Run(() => MercureClient.SubscribeAsync(_config, topics,
                ev => Dispatcher.BeginInvoke(() =>
                {
                    _events.Insert(0, ev);
                    if (_events.Count > 1000) _events.RemoveAt(_events.Count - 1);
                    _subStatus.Text = $"Subscribed to {string.Join(" ", topics)} · {_events.Count} event(s)";
                }),
                () => Dispatcher.BeginInvoke(() => _subStatus.Text = $"Subscribed to {string.Join(" ", topics)} · waiting for updates…"),
                cts.Token));
            if (!cts.IsCancellationRequested) _subStatus.Text = "The hub closed the connection.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested) { _subStatus.Text = "Subscription failed: " + (ex.InnerException?.Message ?? ex.Message); }
        finally
        {
            if (_cts == cts) { _cts = null; _subscribe.Content = "Subscribe"; }
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _subscribe.Content = "Subscribe";
        _subStatus.Text = "Not subscribed";
    }

    private void ShowDetail(MercureEvent? ev)
    {
        if (ev == null) { _detail.Text = ""; return; }
        var data = ev.Data;
        try { data = JsonSerializer.Serialize(JsonDocument.Parse(ev.Data).RootElement, new JsonSerializerOptions { WriteIndented = true }); } catch { }
        _detail.Text = $"// {ev.Received:HH:mm:ss.fff}  id: {ev.Id ?? "-"}  type: {ev.Type ?? "message"}\n{data}";
    }

    private async Task Publish()
    {
        var topics = Topics(_pubTopic.Text);
        if (topics.Length == 0) { _pubResult.Text = "Enter a topic."; return; }
        if (_config.Secret.Length == 0) { _pubResult.Text = "MERCURE_JWT_SECRET is empty in the .env."; return; }
        _config.HubUrl = _hub.Text.Trim();
        _pubResult.Text = "Publishing…";
        try
        {
            var id = await MercureClient.PublishAsync(_config, topics, _pubData.Text, _private.IsChecked == true, _pubType.Text, null);
            _pubResult.Text = "Published: " + id;
            _pubResult.Themed(TextBlock.ForegroundProperty, "SuccessFg");
        }
        catch (Exception ex)
        {
            _pubResult.Text = "Failed: " + (ex.InnerException?.Message ?? ex.Message);
            _pubResult.Themed(TextBlock.ForegroundProperty, "ErrorFg");
        }
    }

    public static void CopyToken(IPluginContext ctx, bool publish)
    {
        var c = MercureConfig.FromProject(ctx.Ide.Project);
        if (c.Secret.Length == 0) { ctx.Ide.Notify("MERCURE_JWT_SECRET is empty in the .env of the project.", NotificationKind.Warning); return; }
        var jwt = Jwt.Create(c.Secret, c.Algorithm, publish ? new[] { "*" } : null, publish ? null : new[] { "*" }, TimeSpan.FromHours(24));
        Clipboard.SetText(jwt);
        ctx.Ide.Notify($"Mercure {(publish ? "publisher" : "subscriber")} JWT copied (all topics, valid 24 h).", NotificationKind.Success);
    }
}
