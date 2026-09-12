using Microsoft.UI.Dispatching;

namespace Tedd.TcpTunnel.ControlPanel.WinUI;

// The native application has no XAML resources; the MAUI App owns the resources and pages.
public sealed class App : MauiWinUIApplication, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    public App()
    {
#if CONTROL_PANEL_SMOKE
        UnhandledException += (_, e) =>
        {
            var file = Path.Combine(Path.GetTempPath(), "Tedd.TcpTunnel.ControlPanel-error.txt");
            File.WriteAllText(file, e.Exception.ToString());
        };
#endif
    }
    private readonly Microsoft.UI.Xaml.Markup.IXamlMetadataProvider[] _providers =
    [
        new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(),
        new Microsoft.Maui.Controls.Controls_Core_XamlTypeInfo.XamlMetaDataProvider(),
        new Microsoft.Maui.Core_XamlTypeInfo.XamlMetaDataProvider()
    ];
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) =>
        _providers.Select(p => p.GetXamlType(type)).FirstOrDefault(t => t is not null)!;
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string name) =>
        _providers.Select(p => p.GetXamlType(name)).FirstOrDefault(t => t is not null)!;
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => [];
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}