using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

/// <summary>SQL console: Ctrl+Enter runs the selection or the statement at the caret; results below.</summary>
public sealed class SqlConsoleView : Grid
{
    private static int _counter;
    private readonly ComboBox _connections = new() { Width = 220, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _databases = new() { Width = 170, Margin = new Thickness(0, 0, 6, 0), IsEditable = true };
    private readonly ICodeEditor _editor;
    private readonly DataGrid _grid = ResultGrid.Create();
    private readonly TextBlock _status = Ui.Text("Ctrl+Enter: run the selection or the statement at the caret", secondary: true);
    private readonly Button _run, _cancel;
    private CancellationTokenSource? _cts;

    public static void Open(ConnectionInfo? conn, string? db, string? sql = null)
    {
        var n = ++_counter;
        DatabasePlugin.Context.Ide.OpenDocument($"sqlconsole:{n}", n == 1 ? "SQL console" : $"SQL console {n}", () => new SqlConsoleView(conn, db, sql), "");
    }

    private SqlConsoleView(ConnectionInfo? conn, string? db, string? sql)
    {
        this.Themed(BackgroundProperty, "EditorBg");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });

        var all = DatabasePlugin.Store.All();
        foreach (var c in all) _connections.Items.Add(new ComboBoxItem { Content = c.Name + "  ·  " + c.Describe(), Tag = c });
        _connections.SelectedItem = _connections.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((ConnectionInfo)i.Tag).Id == conn?.Id)
                                    ?? _connections.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _connections.SelectionChanged += async (_, _) => await LoadDatabases(null);

        _run = Ui.Button("Run", async () => await Run(), primary: true);
        _run.ToolTip = "Ctrl+Enter";
        _cancel = Ui.Button("Cancel", () => _cts?.Cancel());
        _cancel.IsEnabled = false;
        var bar = Ui.Toolbar(_connections, _databases, _run, _cancel,
            Ui.ToolButton("", "Clear the results", () => { ResultGrid.Show(_grid, null); _status.Text = ""; }));
        Children.Add(bar);

        var key = "console:" + (conn?.Id ?? "default");
        _editor = DatabasePlugin.Context.Ide.CreateCodeEditor("sql", sql ?? DatabasePlugin.Context.Settings.Get<string>(key) ?? "SELECT 1;\n");
        _editor.TextChanged += (_, _) => { if (sql == null) DatabasePlugin.Context.Settings.Set(key, _editor.Text); };
        SetRow(_editor.Element, 1);
        Children.Add(_editor.Element);
        _editor.Element.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; await Run(); }
        };

        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.Themed(BackgroundProperty, "SubtleBorder");
        SetRow(splitter, 2);
        Children.Add(splitter);

        var results = new DockPanel();
        var statusBar = new Border { Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(0, 0, 0, 1), Child = _status }.Themed(Border.BorderBrushProperty, "SubtleBorder");
        DockPanel.SetDock(statusBar, Dock.Top);
        results.Children.Add(statusBar);
        results.Children.Add(_grid);
        SetRow(results, 3);
        Children.Add(results);

        Loaded += async (_, _) => { await LoadDatabases(db); _editor.Focus(); };
    }

    private ConnectionInfo? Conn => (_connections.SelectedItem as ComboBoxItem)?.Tag as ConnectionInfo;

    private async Task LoadDatabases(string? select)
    {
        _databases.Items.Clear();
        if (Conn is not { } c) return;
        try
        {
            foreach (var d in await DatabasePlugin.Driver(c).DatabasesAsync()) _databases.Items.Add(d);
            _databases.Text = select ?? (c.Database.Length > 0 ? c.Database : c.Driver == Driver.Sqlite ? "main" : "");
        }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Themed(TextBlock.ForegroundProperty, error ? "ErrorFg" : "FgSecondary");
    }

    private async Task Run()
    {
        if (Conn is not { } c) { SetStatus("Choose a connection (Database tool window › +).", true); return; }
        var sql = _editor.SelectedText.Trim().Length > 0 ? _editor.SelectedText : StatementAt(_editor.Text, _editor.CaretOffset);
        if (sql.Trim().Length == 0) return;
        if (IsDangerous(sql, out var why) && !DatabasePlugin.Context.Ide.Confirm("Run SQL", $"{why}\n\n{Short(sql)}\n\nRun it on {c.Name}?", "Run"))
            return;
        _run.IsEnabled = false;
        _cancel.IsEnabled = true;
        _cts = new CancellationTokenSource();
        SetStatus("Running…");
        try
        {
            var db = c.Driver == Driver.Sqlite || _databases.Text.Trim().Length == 0 ? null : _databases.Text.Trim();
            var r = await DatabasePlugin.Driver(c).QueryAsync(sql, db, 5000, _cts.Token);
            ResultGrid.Show(_grid, r.Table);
            var ms = $"{r.Elapsed.TotalMilliseconds:0} ms";
            SetStatus(r.Table != null
                ? $"{r.Table.Rows.Count:N0} row(s){(r.Truncated ? " (first 5,000 shown)" : "")} · {ms}"
                : $"{(r.Affected >= 0 ? $"{r.Affected:N0} row(s) affected" : "OK")} · {ms}");
        }
        catch (OperationCanceledException) { SetStatus("Cancelled.", true); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            _run.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }

    private static string Short(string sql) => sql.Length > 300 ? sql[..300] + "…" : sql.Trim();

    /// <summary>DROP, TRUNCATE, and DELETE / UPDATE without WHERE ask for a confirmation.</summary>
    internal static bool IsDangerous(string sql, out string why)
    {
        var s = Regex.Replace(sql, @"--[^\n]*|/\*.*?\*/|'(?:''|[^'])*'", " ", RegexOptions.Singleline);
        if (Regex.IsMatch(s, @"\b(DROP|TRUNCATE)\b", RegexOptions.IgnoreCase)) { why = "This statement deletes a table or its whole content."; return true; }
        foreach (var stmt in s.Split(';'))
        {
            if (Regex.IsMatch(stmt, @"^\s*(DELETE|UPDATE)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(stmt, @"\bWHERE\b", RegexOptions.IgnoreCase))
            { why = "DELETE / UPDATE without WHERE changes every row of the table."; return true; }
        }
        why = "";
        return false;
    }

    /// <summary>The statement around the caret (statements separated by ';' outside strings and comments).</summary>
    internal static string StatementAt(string text, int caret)
    {
        var bounds = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '\'' or '"' or '`')
            {
                var q = ch;
                for (i++; i < text.Length && text[i] != q; i++) if (text[i] == '\\') i++;
            }
            else if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-') { while (i < text.Length && text[i] != '\n') i++; }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 1; }
            else if (ch == ';') bounds.Add(i + 1);
        }
        bounds.Add(text.Length);
        for (int b = 0; b < bounds.Count - 1; b++)
        {
            var (start, end) = (bounds[b], bounds[b + 1]);
            var chunk = text[start..end];
            // the caret right after ';' (or on the empty line below) belongs to the previous statement
            if (caret >= start && caret <= end && chunk.Trim().TrimEnd(';').Trim().Length > 0) return chunk.Trim();
            if (caret >= start && caret <= end && b > 0) return text[bounds[b - 1]..bounds[b]].Trim();
        }
        return text.Trim();
    }
}
