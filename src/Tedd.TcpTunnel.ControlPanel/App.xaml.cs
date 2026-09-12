namespace Tedd.TcpTunnel.ControlPanel;
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        SetTheme(Preferences.Default.Get("theme", "System"));
        RequestedThemeChanged += (_, _) => ApplyPalette();
    }

    public void SetTheme(string theme, bool persist = true)
    {
        if (persist) Preferences.Default.Set("theme", theme);
        UserAppTheme = theme switch { "Dark" => AppTheme.Dark, "Light" => AppTheme.Light, _ => AppTheme.Unspecified };
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        var dark = UserAppTheme == AppTheme.Dark || UserAppTheme == AppTheme.Unspecified && RequestedTheme == AppTheme.Dark;
        var keys = new[] { "Canvas", "Surface", "Line", "Accent", "Ink", "Muted", "ButtonSurface", "Field", "Sidebar", "PrimaryText" };
        var colors = dark ?
            new[] { "#172431", "#1C2B38", "#2B3D4B", "#18B563", "#EEF3F5", "#9AA8B2", "#253847", "#223340", "#243645", "#07150D" } :
            new[] { "#EEF2F3", "#FAFBFB", "#CAD5DA", "#0B8045", "#172630", "#60727D", "#DFE7EA", "#E5EBED", "#E1E8EA", "#FFFFFF" };
        for (var i = 0; i < keys.Length; i++) Resources[keys[i]] = Color.FromArgb(colors[i]);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page) { Title = "Tedd TcpTunnel · Control panel", Width = 1440, Height = 960, MinimumWidth = 1120, MinimumHeight = 740 };
        TrayIcon? tray = null;
        window.Created += (_, _) =>
        {
            var native = (Microsoft.UI.Xaml.Window)window.Handler!.PlatformView!;
            tray = new TrayIcon(native, async () => await page.RequestExitAsync());
            page.MinimizeRequested += (_, _) => tray.Hide();
            page.ExitConfirmed += (_, _) => tray.PrepareExit();
        };
        window.Destroying += (_, _) => { tray?.Dispose(); page.Shutdown(); };
        return window;
    }
}
