using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

/// <summary>Editor tab of a table: Data (filter, sort, pages), Structure, DDL.</summary>
public sealed class TableView : DockPanel
{
    private const int PageSize = 200;
    private readonly ConnectionInfo _conn;
    private readonly string _db, _table;
    private readonly DataGrid _grid = ResultGrid.Create();
    private readonly TextBox _where = Ui.TextBox(width: 320, tooltip: "WHERE clause, e.g. status = 'active' AND id > 10 (Enter to apply)");
    private readonly TextBox _order = Ui.TextBox(width: 160, tooltip: "ORDER BY, e.g. id DESC (Enter to apply)");
    private readonly TextBlock _info = Ui.Text("", secondary: true);
    private int _page;
    private long _total;

    public static void Open(ConnectionInfo conn, string db, string table) =>
        DatabasePlugin.Context.Ide.OpenDocument($"db:{conn.Id}:{db}:{table}", table, () => new TableView(conn, db, table), "");

    private TableView(ConnectionInfo conn, string db, string table)
    {
        _conn = conn;
        _db = db;
        _table = table;
        this.Themed(BackgroundProperty, "EditorBg");

        var tabs = new TabControl { Style = Ui.Style("ToolTabControl"), BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Data", Content = BuildData(), Style = Ui.Style("ToolTabItem") });
        var structure = ResultGrid.Create();
        var ddl = DatabasePlugin.Context.Ide.CreateCodeEditor("sql", "-- loading…");
        tabs.Items.Add(new TabItem { Header = "Structure", Content = structure, Style = Ui.Style("ToolTabItem") });
        tabs.Items.Add(new TabItem { Header = "DDL", Content = ddl.Element, Style = Ui.Style("ToolTabItem") });
        Children.Add(tabs);

        Loaded += async (_, _) =>
        {
            await LoadPage();
            try
            {
                var d = DatabasePlugin.Driver(conn);
                var cols = await d.ColumnsAsync(db, table);
                var t = new DataTable();
                foreach (var (i, name) in new[] { "Column", "Type", "Nullable", "Key", "Default", "Extra" }.Select((n, i) => (i, n)))
                    t.Columns.Add(new DataColumn("c" + i, typeof(string)) { Caption = name });
                foreach (var c in cols) t.Rows.Add(c.Name, c.Type, c.Nullable ? "YES" : "NO", c.Key, (object?)c.Default ?? DBNull.Value, c.Extra);
                ResultGrid.Show(structure, t);
                ddl.Text = await d.DdlAsync(db, table);
            }
            catch (Exception ex) { ddl.Text = "-- " + ex.Message; }
        };
    }

    private FrameworkElement BuildData()
    {
        var panel = new DockPanel();
        _where.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { _page = 0; await LoadPage(); } };
        _order.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { _page = 0; await LoadPage(); } };
        var bar = Ui.Toolbar(
            Ui.Text("WHERE ", secondary: true), _where,
            Ui.Text("ORDER BY ", secondary: true), _order,
            Ui.ToolButton("", "Refresh (F5)", async () => await LoadPage()),
            Ui.Separator(),
            Ui.ToolButton("", "Previous page", async () => { if (_page > 0) { _page--; await LoadPage(); } }),
            Ui.ToolButton("", "Next page", async () => { if ((_page + 1) * PageSize < _total) { _page++; await LoadPage(); } }),
            _info,
            Ui.Separator(),
            Ui.ToolButton("", "Open in a SQL console", () => SqlConsoleView.Open(_conn, _db, Sql(includePaging: true) + ";")));
        SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(_grid);
        panel.KeyDown += async (_, e) => { if (e.Key == Key.F5) await LoadPage(); };
        return panel;
    }

    private string Sql(bool includePaging)
    {
        var d = DatabasePlugin.Driver(_conn);
        var sql = $"SELECT * FROM {d.Qualified(_db, _table)}";
        if (_where.Text.Trim().Length > 0) sql += " WHERE " + _where.Text.Trim();
        if (_order.Text.Trim().Length > 0) sql += " ORDER BY " + _order.Text.Trim();
        if (includePaging) sql += $" LIMIT {PageSize} OFFSET {_page * PageSize}";
        return sql;
    }

    private async Task LoadPage()
    {
        _info.Text = "  loading…";
        try
        {
            var d = DatabasePlugin.Driver(_conn);
            var where = _where.Text.Trim().Length > 0 ? " WHERE " + _where.Text.Trim() : "";
            _total = Convert.ToInt64(await d.ScalarAsync($"SELECT COUNT(*) FROM {d.Qualified(_db, _table)}{where}", _db) ?? 0L);
            var r = await d.QueryAsync(Sql(includePaging: true), _db, PageSize);
            ResultGrid.Show(_grid, r.Table);
            var from = _total == 0 ? 0 : _page * PageSize + 1;
            var to = Math.Min(_total, (long)(_page + 1) * PageSize);
            _info.Text = $"  {from:N0}–{to:N0} of {_total:N0} rows · {r.Elapsed.TotalMilliseconds:0} ms";
            _info.Themed(TextBlock.ForegroundProperty, "FgSecondary");
        }
        catch (Exception ex)
        {
            _info.Text = "  " + ex.Message;
            _info.Themed(TextBlock.ForegroundProperty, "ErrorFg");
        }
    }
}
