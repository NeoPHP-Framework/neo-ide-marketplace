using System.Windows;
using System.Windows.Controls;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

/// <summary>Add / edit a MySQL or SQLite connection.</summary>
public static class ConnectionForm
{
    public static bool Edit(IIde ide, ConnectionStore store, ConnectionInfo? existing)
    {
        var c = existing ?? new ConnectionInfo { Name = "MySQL local" };
        var driver = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        driver.Items.Add("MySQL / MariaDB");
        driver.Items.Add("SQLite");
        driver.SelectedIndex = c.Driver == Driver.Sqlite ? 1 : 0;
        var name = Ui.TextBox(c.Name);
        var host = Ui.TextBox(c.Host);
        var port = Ui.TextBox(c.Port.ToString(), 80);
        var user = Ui.TextBox(c.User);
        var password = new PasswordBox { Password = existing != null ? store.Password(existing) ?? "" : "", Margin = new Thickness(0, 0, 6, 0) };
        var database = Ui.TextBox(c.Database, tooltip: "Empty = every database of the server");
        var file = Ui.TextBox(c.File);
        var status = Ui.Text("", secondary: true);
        status.TextWrapping = TextWrapping.Wrap;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var mysqlRows = new List<UIElement>();
        var sqliteRows = new List<UIElement>();
        void Row(string label, FrameworkElement control, List<UIElement>? group = null)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Ui.Text(label);
            l.Margin = new Thickness(0, 4, 8, 4);
            control.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(l, r);
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            grid.Children.Add(l);
            grid.Children.Add(control);
            group?.Add(l);
            group?.Add(control);
        }
        Row("Type", driver);
        Row("Name", name);
        Row("Host", host, mysqlRows);
        Row("Port", port, mysqlRows);
        Row("User", user, mysqlRows);
        Row("Password", password, mysqlRows);
        Row("Database", database, mysqlRows);
        var browse = new DockPanel();
        var pick = Ui.Button("…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SQLite (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files|*.*" };
            if (dlg.ShowDialog() == true) file.Text = dlg.FileName;
        });
        pick.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(pick, Dock.Right);
        browse.Children.Add(pick);
        browse.Children.Add(file);
        Row("File", browse, sqliteRows);

        void Sync()
        {
            var sqlite = driver.SelectedIndex == 1;
            foreach (var e in mysqlRows) e.Visibility = sqlite ? Visibility.Collapsed : Visibility.Visible;
            foreach (var e in sqliteRows) e.Visibility = sqlite ? Visibility.Visible : Visibility.Collapsed;
        }
        driver.SelectionChanged += (_, _) => Sync();
        Sync();

        ConnectionInfo Build() => new()
        {
            Id = c.Id, Name = name.Text.Trim(), Driver = driver.SelectedIndex == 1 ? Driver.Sqlite : Driver.MySql,
            Host = host.Text.Trim(), Port = int.TryParse(port.Text, out var p) ? p : 3306, User = user.Text.Trim(),
            Database = database.Text.Trim(), File = file.Text.Trim(),
        };

        var test = Ui.Button("Test connection", async () =>
        {
            status.Text = "Connecting…";
            var info = Build();
            await using var d = DbDriver.Create(info, password.Password);
            try
            {
                await d.OpenAsync();
                status.Text = $"Connected: {(info.Driver == Driver.Sqlite ? "SQLite " : "server ")}{d.ServerVersion}";
            }
            catch (Exception ex) { status.Text = "Failed: " + ex.Message; }
        });
        test.Margin = new Thickness(0, 10, 0, 6);
        test.HorizontalAlignment = HorizontalAlignment.Left;

        var content = new StackPanel();
        content.Children.Add(grid);
        content.Children.Add(test);
        content.Children.Add(status);

        var ok = ide.ShowDialog(existing == null ? "New connection" : "Edit connection", content, "Save", "Cancel", () =>
        {
            var b = Build();
            if (b.Name.Length == 0) return "Give the connection a name.";
            if (b.Driver == Driver.MySql && b.Host.Length == 0) return "The host is required.";
            if (b.Driver == Driver.Sqlite && b.File.Length == 0) return "Choose the SQLite file.";
            return null;
        }, 480);
        if (!ok) return false;
        store.Save(Build(), driver.SelectedIndex == 1 ? null : password.Password);
        DatabasePlugin.Disconnect(c);
        return true;
    }
}
