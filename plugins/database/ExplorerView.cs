using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

/// <summary>Left tool window: connections › databases › tables / views › columns.</summary>
public sealed class ExplorerView : DockPanel
{
    private sealed record ConnNode(ConnectionInfo Conn);
    private sealed record DbNode(ConnectionInfo Conn, string Database);
    private sealed record TableNode(ConnectionInfo Conn, string Database, DbObject Table);
    private sealed record ColumnNode(DbColumn Column);

    private readonly IPluginContext _ctx;
    private readonly ConnectionStore _store;
    private readonly TreeView _tree = new() { Padding = new Thickness(0, 4, 0, 8) };
    private readonly ContentControl _empty = new();

    public ExplorerView(IPluginContext ctx, ConnectionStore store)
    {
        _ctx = ctx;
        _store = store;
        var bar = Ui.Toolbar(
            Ui.ToolButton("", "New connection…", () => { if (ConnectionForm.Edit(ctx.Ide, store, null)) Reload(); }),
            Ui.ToolButton("", "Refresh", () => { foreach (var c in store.All()) DatabasePlugin.Disconnect(c); Reload(); }),
            Ui.ToolButton("", "New SQL console", () => SqlConsoleView.Open(Selected()?.conn, Selected()?.db)),
            Ui.ToolButton("", "Collapse all", () => { foreach (TreeViewItem i in _tree.Items) i.IsExpanded = false; }));
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var grid = new Grid();
        grid.Children.Add(_tree);
        grid.Children.Add(_empty);
        Children.Add(grid);
        _tree.MouseDoubleClick += OnDoubleClick;
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnDoubleClick(_tree, null); };
        store.Changed += () => Dispatcher.BeginInvoke(Reload);
        Reload();
    }

    private (ConnectionInfo conn, string? db)? Selected() => (_tree.SelectedItem as TreeViewItem)?.Tag switch
    {
        ConnNode n => (n.Conn, n.Conn.Database.Length > 0 ? n.Conn.Database : null),
        DbNode n => (n.Conn, n.Database),
        TableNode n => (n.Conn, n.Database),
        _ => null,
    };

    private void Reload()
    {
        _tree.Items.Clear();
        var all = _store.All();
        foreach (var c in all) _tree.Items.Add(ConnItem(c));
        _empty.Content = all.Count > 0 ? null : Ui.Empty(_ctx.Ide.Project == null
            ? "Open a NeoPHP project to use its database."
            : "DATABASE_URL of the project is not a MySQL / SQLite database.\nAdd a connection with +.");
        // the project connection is opened right away
        if (_tree.Items.Count > 0 && ((TreeViewItem)_tree.Items[0]).Tag is ConnNode { Conn.FromEnv: true }) ((TreeViewItem)_tree.Items[0]).IsExpanded = true;
    }

    // ------------------------------------------------------------------ items

    private static FrameworkElement Header(string glyph, string colorHex, string text, string? secondary = null, bool bold = false)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), Foreground = new SolidColorBrush(Ui.Hex(colorHex)), VerticalAlignment = VerticalAlignment.Center });
        var t = Ui.Text(text);
        if (bold) t.FontWeight = FontWeights.SemiBold;
        p.Children.Add(t);
        if (secondary != null)
        {
            var s = Ui.Text("  " + secondary, secondary: true, size: 11.5);
            p.Children.Add(s);
        }
        return p;
    }

    private TreeViewItem Lazy(object tag, FrameworkElement header, Func<TreeViewItem, Task> load)
    {
        var item = new TreeViewItem { Header = header, Tag = tag };
        item.Items.Add(new TreeViewItem { Header = Ui.Text("Loading…", secondary: true), IsEnabled = false });
        var loaded = false;
        item.Expanded += async (_, e) =>
        {
            if (e.OriginalSource != item || loaded) return;
            loaded = true;
            try
            {
                await load(item);
            }
            catch (Exception ex)
            {
                item.Items.Clear();
                var err = Ui.Text(ex.Message);
                err.Themed(TextBlock.ForegroundProperty, "ErrorFg");
                err.TextWrapping = TextWrapping.Wrap;
                err.MaxWidth = 320;
                item.Items.Add(new TreeViewItem { Header = err });
                loaded = false;
            }
        };
        return item;
    }

    private TreeViewItem ConnItem(ConnectionInfo c)
    {
        var item = Lazy(new ConnNode(c), Header(c.Driver == Driver.Sqlite ? "" : "", c.Driver == Driver.Sqlite ? "#4C9BE8" : "#E8A33D", c.Name, c.Describe(), bold: true), async node =>
        {
            var d = DatabasePlugin.Driver(c);
            var dbs = await d.DatabasesAsync();
            node.Items.Clear();
            var hidden = new[] { "information_schema", "performance_schema", "mysql", "sys" };
            foreach (var db in dbs.OrderBy(x => hidden.Contains(x) ? 1 : 0).ThenBy(x => x))
            {
                var dbItem = DbItem(c, db, hidden.Contains(db));
                node.Items.Add(dbItem);
                if (db == c.Database || (c.Driver == Driver.Sqlite)) dbItem.IsExpanded = true;
            }
            if (c.Driver == Driver.MySql) _ctx.Ide.Notify($"{c.Name}: MySQL {d.ServerVersion}");
        });
        var menu = new ContextMenu();
        Add(menu, "New SQL console", () => SqlConsoleView.Open(c, c.Database.Length > 0 ? c.Database : null));
        Add(menu, "Refresh", () => { DatabasePlugin.Disconnect(c); Reload(); });
        Add(menu, "Disconnect", () => { DatabasePlugin.Disconnect(c); item.IsExpanded = false; Reload(); });
        if (!c.FromEnv)
        {
            menu.Items.Add(new Separator());
            Add(menu, "Edit…", () => { if (ConnectionForm.Edit(_ctx.Ide, _store, c)) Reload(); });
            Add(menu, "Remove", () => { if (_ctx.Ide.Confirm("Remove connection", $"Remove the connection '{c.Name}'?", "Remove")) { DatabasePlugin.Disconnect(c); _store.Remove(c); } });
        }
        else
        {
            menu.Items.Add(new Separator());
            Add(menu, "Open .env", () => { if (_ctx.Ide.Project is { } p) _ctx.Ide.OpenFile(System.IO.Path.Combine(p.Root, ".env")); });
        }
        item.ContextMenu = menu;
        return item;
    }

    private TreeViewItem DbItem(ConnectionInfo c, string db, bool system)
    {
        var item = Lazy(new DbNode(c, db), Header("", system ? "#868A91" : "#6AAB73", db), async node =>
        {
            var tables = await DatabasePlugin.Driver(c).TablesAsync(db);
            node.Items.Clear();
            foreach (var t in tables) node.Items.Add(TableItem(c, db, t));
            if (tables.Count == 0) node.Items.Add(new TreeViewItem { Header = Ui.Text("No table", secondary: true), IsEnabled = false });
        });
        var menu = new ContextMenu();
        Add(menu, "New SQL console", () => SqlConsoleView.Open(c, db));
        Add(menu, "Copy name", () => Clipboard.SetText(db));
        item.ContextMenu = menu;
        return item;
    }

    private TreeViewItem TableItem(ConnectionInfo c, string db, DbObject t)
    {
        var item = Lazy(new TableNode(c, db, t), Header(t.IsView ? "" : "", t.IsView ? "#C77DBB" : "#56A8F5", t.Name, t.Rows is { } r ? $"~{r:N0}" : null), async node =>
        {
            var cols = await DatabasePlugin.Driver(c).ColumnsAsync(db, t.Name);
            node.Items.Clear();
            foreach (var col in cols)
            {
                var key = col.Key switch { "PRI" => ("", "#E8C63D"), "UNI" => ("", "#868A91"), "MUL" => ("", "#868A91"), _ => ("", "#868A91") };
                node.Items.Add(new TreeViewItem { Header = Header(key.Item1, key.Item2, col.Name, col.Type + (col.Nullable ? "" : " not null")), Tag = new ColumnNode(col) });
            }
        });
        var menu = new ContextMenu();
        Add(menu, "Open data", () => TableView.Open(c, db, t.Name));
        Add(menu, "New SQL console", () => SqlConsoleView.Open(c, db, $"SELECT * FROM {DatabasePlugin.Driver(c).Qualified(db, t.Name)} LIMIT 100;"));
        Add(menu, "Copy name", () => Clipboard.SetText(t.Name));
        item.ContextMenu = menu;
        return item;
    }

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs? e)
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: TableNode n })
        {
            TableView.Open(n.Conn, n.Database, n.Table.Name);
            if (e != null) e.Handled = true;
        }
    }
}
