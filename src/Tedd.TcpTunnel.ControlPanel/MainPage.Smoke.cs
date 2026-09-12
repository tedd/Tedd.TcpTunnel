#if CONTROL_PANEL_SMOKE
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Tedd.TcpTunnel.Management;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Tedd.TcpTunnel.ControlPanel;

public partial class MainPage
{
    private async Task RunSmokeAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var previousConfigPreference = Preferences.Default.Get("configPath", "");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        var echoTask = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await echo.AcceptTcpClientAsync(stop.Token);
                var buffer = new byte[262144];
                int read;
                while ((read = await client.GetStream().ReadAsync(buffer, stop.Token)) > 0)
                    await client.GetStream().WriteAsync(buffer.AsMemory(0, read), stop.Token);
            }
        });
        var server = new Listener(new() { Name = "server", Mode = TunnelMode.Server, Compression = Codec.Lz4,
            ListenPort = 0, RemotePort = ((IPEndPoint)echo.LocalEndpoint).Port });
        var serverTask = server.Start(stop.Token);
        try
        {
            var serverEndpoint = await server.Ready;
            ((App)Application.Current!).SetTheme("Dark", false);
            _building = true; ThemePicker.SelectedItem = "Dark"; RunMode.SelectedIndex = 1; _building = false;
            _path = Path.Combine(output, "smoke.json");
            var options = new TunnelOptions { Forwards = [new() { Name = "application-link", Mode = TunnelMode.Client,
                Compression = Codec.Lz4, ListenPort = 0, RemotePort = serverEndpoint.Port }],
                Update = new() { CheckOnStartup = false }, Logging = new() { Console = false } };
            _document = ConfigurationFile.Save(_path, JsonSerializer.Serialize(options, ConfigurationFile.Json),
                File.Exists(_path) ? ConfigurationFile.Read(_path).Revision : null);
            LoadLocal();
            // Exercises every form field, including flags, nullable values and nested collections.
            ApplyForms(); _draft.Validate();
            await StartAsync();
            using var source = new TcpClient();
            await source.ConnectAsync(IPEndPoint.Parse(_status!.Forwards[0].ListenEndpoint), stop.Token);
            var pump = Task.Run(async () =>
            {
                var data = new byte[262144]; new Random(42).NextBytes(data.AsSpan(0, data.Length / 3));
                var received = new byte[data.Length];
                for (var i = 0; i < 800 && !stop.IsCancellationRequested; i++)
                {
                    await source.GetStream().WriteAsync(data, stop.Token);
                    await source.GetStream().ReadExactlyAsync(received, stop.Token);
                    if (!data.AsSpan().SequenceEqual(received)) throw new InvalidDataException("UI smoke payload mismatch.");
                    await Task.Delay(8 + (int)(18 * (1 + Math.Sin(i / 40.0))), stop.Token);
                }
            });
            for (var i = 0; i < 24; i++) { await Task.Delay(1000, stop.Token); await RefreshAsync(); }
            if (_status is null || _history.For("application-link").Count < 10) throw new InvalidOperationException("No live telemetry reached the UI.");
            await CaptureAsync(Path.Combine(output, "control-panel.png"));
            ShowPage("Forwards"); await Task.Delay(500, stop.Token);
            await CaptureAsync(Path.Combine(output, "configuration.png"));
            ShowPage("Overview");
            var oldId = _status.InstanceId;
            static IEnumerable<IVisualTreeElement> Descendants(IVisualTreeElement element) =>
                new[] { element }.Concat(element.GetVisualChildren().SelectMany(Descendants));
            var nameEntry = Descendants(_forwardForm!).OfType<Entry>().Single(e => SemanticProperties.GetDescription(e) == "Name");
            nameEntry.Text = "application-link-saved";
            if (!_dirty) throw new InvalidOperationException("Editing a configuration field did not mark the form dirty.");
            await SaveAsync(false);
            if (_status is null || !_status.PendingConfiguration ||
                ConfigurationFile.Parse(ConfigurationFile.Read(_path).Json).Forwards[0].Name != "application-link-saved")
                throw new InvalidOperationException("Edited configuration was not saved and marked pending.");
            await StopAsync();
            // Closing sockets terminates the traffic generator; all later checks use a fresh session.
            try { await pump; } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
            await StartAsync();
            if (_status is null || _status.InstanceId == oldId || _status.PendingConfiguration || _status.Forwards[0].Name != "application-link-saved")
                throw new InvalidOperationException("Daemon restart did not activate the saved configuration.");
            await StopAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: configuration forms, foreground daemon, live compressed bidirectional traffic, saved configuration and restart.");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), ex.ToString()); }
        finally
        {
            if (previousConfigPreference.Length == 0) Preferences.Default.Remove("configPath");
            else Preferences.Default.Set("configPath", previousConfigPreference);
            await stop.CancelAsync(); echo.Stop(); await _shutdown.CancelAsync();
            if (_foregroundTask is not null) await _foregroundTask;
            try { await Task.WhenAll(echoTask, serverTask); } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
            ExitConfirmed?.Invoke(this, EventArgs.Empty); Application.Current!.Quit();
        }
    }

    private async Task CaptureAsync(string path)
    {
        var native = (Microsoft.UI.Xaml.Window)Window.Handler!.PlatformView!;
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(native.Content);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
#endif
