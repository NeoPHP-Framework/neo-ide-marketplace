using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

public sealed class DatabasePlugin : IPlugin
{
    internal static IPluginContext Context { get; private set; } = null!;
    internal static ConnectionStore Store { get; private set; } = null!;
    private static readonly Dictionary<string, DbDriver> Drivers = new();

    public void Activate(IPluginContext context)
    {
        Context = context;
        Store = new ConnectionStore(context);
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "database", Title = "Database", Icon = "", Location = ToolWindowLocation.Left,
            Create = () => new ExplorerView(context, Store),
        });
        context.Ide.AddMenuItem("New SQL console", () => SqlConsoleView.Open(null, null), "");
        context.Ide.ProjectOpened += _ => { DisconnectAll(); Store.RaiseChanged(); };
        context.Ide.ProjectClosed += DisconnectAll;
    }

    public void Deactivate() => DisconnectAll();

    /// <summary>One shared connection per connection id (the explorer, the table views and the consoles use it).</summary>
    internal static DbDriver Driver(ConnectionInfo c)
    {
        if (!Drivers.TryGetValue(c.Id, out var d))
        {
            d = DbDriver.Create(c, Store.Password(c));
            Drivers[c.Id] = d;
        }
        return d;
    }

    internal static void Disconnect(ConnectionInfo c)
    {
        if (Drivers.Remove(c.Id, out var d)) _ = d.DisposeAsync();
    }

    private static void DisconnectAll()
    {
        foreach (var d in Drivers.Values) _ = d.DisposeAsync();
        Drivers.Clear();
    }
}
