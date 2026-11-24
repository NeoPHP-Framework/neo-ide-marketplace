# NeoIDE plugins

| Plugin | Where | What |
|---|---|---|
| [Database](database) | left bar | MySQL / MariaDB and SQLite: connection from `DATABASE_URL`, schema tree, table data (filter, sort, pages), structure, DDL, SQL console (Ctrl+Enter), CSV export. Saved connections keep their password encrypted (Windows DPAPI). |
| [Mercure](mercure) | bottom bar | Hub status, live subscription to topics (SSE), JSON detail of each update, publication of test updates, publisher / subscriber JWT from `MERCURE_JWT_SECRET`. |
| [Routes](routes) | left bar | `route:list` in a searchable panel; double-click opens the controller method, GET routes open in the browser (asks the `{parameters}`). |
| [Logs](logs) | bottom bar | `var/log/*.log` followed live, levels coloured and filtered, search, pretty JSON context. |
| [NeoPHP Toolbox](toolbox) | left bar | `doctor` report, migrations and their state, queue and scheduler status, cache / assets / maintenance mode. |

Install them from **NeoIDE › Settings › Plugins** (or Tools › Plugins…).

## How a plugin works

A plugin is a .NET 11 WPF class library loaded by NeoIDE in its own `AssemblyLoadContext`:

- `plugin.json` — the manifest ([schema](../schemas/plugin.schema.json));
- a class implementing `NeoIDE.Sdk.IPlugin`, compiled against **NeoIDE.Sdk** (the API of the IDE, in the
  [NeoIDE repository](https://github.com/NeoPHP-Framework/NeoIDE/tree/main/NeoIDE.Sdk));
- its NuGet dependencies, shipped next to it (MySqlConnector, Microsoft.Data.Sqlite… for Database).

```csharp
public sealed class HelloPlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "hello", Title = "Hello", Icon = "\uE8BD", Location = ToolWindowLocation.Left,
            Create = () => new HelloView(context),          // any WPF element
        });
        context.Ide.AddMenuItem("Say hello", () => context.Ide.Notify("Hello!", NotificationKind.Success));
    }
}
```

`IIde` (the `context.Ide` object) gives:

| | |
|---|---|
| `Project` · `ProjectOpened` · `ProjectClosed` | root, name, PHP, **`Env`** (the .env files merged like NeoPHP, `%kernel.root_path%` resolved) |
| `AddToolWindow` · `ShowToolWindow` | panels of the left / bottom bars |
| `OpenDocument` · `CloseDocument` | your view in a tab of the editor area |
| `AddMenuItem` | entry of the **Tools** menu |
| `RunNeoAsync("route:list")` | `php bin/neo … --no-ansi --no-interaction`, output captured |
| `RunNeoInConsole("make:migration")` | interactive NeoConsole, visible to the user |
| `OpenFile` · `GoToClass(fqcn, member)` | navigation (uses the PHP index of the IDE) |
| `CreateCodeEditor("sql")` | the IDE editor (highlighting, theme, font) |
| `Confirm` · `Prompt` · `ShowDialog` · `Notify` | themed dialogs, status bar |
| `Brush(key)` · `ThemeChanged` · `IsDarkTheme` | theme |

`context.Settings` stores the plugin settings (`Get` / `Set`, `GetSecret` / `SetSecret` encrypted for the Windows account).
Helpers: `Ui` (toolbars, buttons, badges, themed texts, DataGrid), `ConsoleTable.Parse` (tables printed by bin/neo),
`DotEnv.Load`. Bind colours with `element.Themed(property, "Fg")` so the view follows the theme.

## Create a plugin

1. Copy a small plugin (`routes`) to `plugins/<id>/`, rename the `.csproj`, edit `plugin.json` (`id` = folder name).
   `Directory.Build.props` already sets the target framework, the SDK reference and the packaging.
2. Build and test locally (the SDK comes from a NeoIDE checkout):

   ```powershell
   $env:NeoIdeRoot = "C:\path\to\NeoIDE"
   pwsh tools/build-plugins.ps1 -Only <id>     # → dist/<id>-<version>.zip
   ```

   In NeoIDE: **Settings › Plugins › Install from disk…** and pick the zip.
3. Run `pwsh tools/build-index.ps1`, commit `plugins/<id>/` **and** `index.json`, open a pull request.

After the merge, the **Plugins** workflow builds the zip and attaches it to the
[`plugins` release](https://github.com/NeoPHP-Framework/neo-ide-marketplace/releases/tag/plugins); NeoIDE downloads it from there.
A published version is never replaced: **increase `version`** in `plugin.json` to publish a change — NeoIDE then offers the update.

Plugins run with the permissions of the user: review the code of every plugin before merging it.
