using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Mercure;

public sealed class MercurePlugin : IPlugin
{
    public void Activate(IPluginContext context)
    {
        context.Ide.AddToolWindow(new ToolWindow
        {
            Id = "mercure", Title = "Mercure", Icon = "", Location = ToolWindowLocation.Bottom,
            Create = () => new MercureView(context),
        });
        context.Ide.AddMenuItem("Copy Mercure subscriber JWT", () => MercureView.CopyToken(context, publish: false), "");
        context.Ide.AddMenuItem("Copy Mercure publisher JWT", () => MercureView.CopyToken(context, publish: true), "");
    }
}
