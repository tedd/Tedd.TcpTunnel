using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.ControlPanel;

public partial class MainPage
{
    private async Task StartAsync()
    {
        if (_dirty || !File.Exists(_path)) await SaveAsync(false);
        if (ServiceMode)
        {
            if (_foregroundTask is { IsCompleted: false })
                throw new InvalidOperationException("Stop the app daemon before starting the Windows service.");
            if (!_service.Installed) await InstallServiceAsync();
            else await WindowsOperations.SetServiceAsync(_serviceName, "start");
        }
        else
        {
            if (_service.State is "Running" or "Starting") throw new InvalidOperationException("Stop the Windows service before starting the app daemon.");
            if (_status is not null || _foregroundTask is { IsCompleted: false }) throw new InvalidOperationException("An app daemon is already running for this configuration.");
            _foregroundStop?.Dispose();
            _foregroundStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var token = _foregroundStop.Token; var path = _path;
            _foregroundTask = Task.Run(() => Tedd.TcpTunnel.Console.Program.RunAsync(["--config", path], token: token));
            await Task.Delay(400, _shutdown.Token);
            if (_foregroundTask.IsCompleted) throw new InvalidOperationException("The app daemon could not start. Validate the configuration and check for occupied ports.");
        }
        await RefreshAsync();
    }

    private async Task StopAsync()
    {
        if (ServiceMode) await WindowsOperations.SetServiceAsync(_serviceName, "stop");
        else if (_foregroundTask is not null)
        {
            await _foregroundStop!.CancelAsync(); await _foregroundTask; _foregroundTask = null;
        }
        else if (_status is not null)
        {
            await SendControlAsync(new("stop"), _shutdown.Token);
            await Task.Delay(500, _shutdown.Token);
        }
        _history.Reset(); await RefreshAsync();
    }

    private async Task RestartAsync()
    {
        if (_dirty) await SaveAsync(false);
        await StopAsync(); await StartAsync();
        Message.Text = "Restarted with the saved configuration.";
    }

    private async Task InstallServiceAsync()
    {
        if (_foregroundTask is { IsCompleted: false })
        {
            await _foregroundStop!.CancelAsync(); await _foregroundTask; _foregroundTask = null;
        }
        if (!File.Exists(_path) || _dirty) await SaveAsync(false);
        WindowsOperations.ValidateName(ServiceName.Text); _serviceName = ServiceName.Text;
        var executable = Path.GetFullPath(DaemonPath.Text);
        if (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), "tcptunnel.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the installed or published tcptunnel.exe in Application settings.");
        await WindowsOperations.RunAsync(executable, ["--install-service", "--service-name", _serviceName, "--config", _path]);
        await WindowsOperations.WaitAsync(_serviceName, "Running");
        _building = true; RunMode.SelectedIndex = 0; _building = false;
        await RefreshAsync();
    }

    private void OnStart(object? sender, EventArgs e) => _ = ActionAsync(StartAsync);
    private void OnStop(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (await DisplayAlertAsync("Stop runtime", "Active connections will close.", "Stop", "Cancel")) await StopAsync();
    });
    private void OnRestart(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (await DisplayAlertAsync("Restart runtime", "Active connections will close and the saved configuration will be loaded.", "Restart", "Cancel")) await RestartAsync();
    });
    private void OnResetConnections(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        var name = TrafficForward.SelectedItem as string ?? throw new InvalidOperationException("Select a running forward.");
        if (await DisplayAlertAsync("Restart connections", "Disconnect all sessions for " + name + "? Clients must reconnect. Listener settings stay active.", "Disconnect", "Cancel"))
        { await SendControlAsync(new("restart-connections", Forward: name == TelemetryHistory.AllForwards ? null : name), _shutdown.Token); await RefreshAsync(); }
    });
    private void OnRunModeChanged(object? sender, EventArgs e)
    {
        if (_building || !_initialized) return;
        _status = null; _history.Reset();
        _ = ActionAsync(async () =>
        {
            var previousPath = _path;
            await RefreshAsync();
            if (_path != previousPath) LoadLocal();
        });
    }
    private void OnTrafficForwardChanged(object? sender, EventArgs e) => UpdateCharts();
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!_building && ThemePicker.SelectedItem is string theme) ((App)Application.Current!).SetTheme(theme);
    }
    private void OnInstallService(object? sender, EventArgs e) => _ = ActionAsync(InstallServiceAsync);
    private void OnUninstallService(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (!_service.Installed) throw new InvalidOperationException("The service is not installed.");
        if (!await DisplayAlertAsync("Uninstall service", "Stop and remove " + _serviceName + "? The configuration file is retained.", "Uninstall", "Cancel")) return;
        await WindowsOperations.RunAsync(_service.Executable!, ["--uninstall-service", "--service-name", _serviceName]);
        await RefreshAsync();
    });
    private void OnStartup(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (!_service.Installed) await InstallServiceAsync();
        else await WindowsOperations.SetStartupAsync(_serviceName, !_service.Automatic);
        await RefreshAsync();
    });
    private void OnTray(object? sender, EventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
    private void OnExit(object? sender, EventArgs e) => _ = ActionAsync(ExitAsync);
    public Task RequestExitAsync() => ActionAsync(ExitAsync);
    public async Task ExitAsync()
    {
        if (_dirty && !await DisplayAlertAsync("Unsaved configuration", "Exit and discard unsaved edits?", "Exit", "Cancel")) return;
        if (_foregroundTask is { IsCompleted: false } &&
            !await DisplayAlertAsync("Stop app daemon", "Exiting stops the app daemon and its active connections. A Windows service continues running.", "Stop and exit", "Cancel")) return;
        _timer?.Stop(); await _shutdown.CancelAsync();
        if (_foregroundTask is not null) await _foregroundTask;
        ExitConfirmed?.Invoke(this, EventArgs.Empty);
        Application.Current!.Quit();
    }
    public void Shutdown() { _timer?.Stop(); _shutdown.Cancel(); }
}
