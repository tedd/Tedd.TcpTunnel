using System.Text.Json;
using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.ControlPanel;

public partial class MainPage : ContentPage
{
    private TunnelOptions _draft = new() { Forwards = [new()] };
    private ConfigurationDocument? _document;
    private OptionForm? _forwardForm, _applicationForm;
    private readonly TelemetryHistory _history = new();
    private readonly TrafficChart _outbound = new("OUTBOUND  /  LISTENER → DESTINATION", true);
    private readonly TrafficChart _inbound = new("INBOUND  /  DESTINATION → LISTENER", false);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _foregroundStop;
    private Task<int>? _foregroundTask;
    private IDispatcherTimer? _timer;
    private Task _pollTask = Task.CompletedTask;
    private DaemonStatus? _status;
    private ServiceState _service = new(false, "Not installed", false, null, null);
    private string _serviceName = "Tedd.TcpTunnel", _path = "";
    private bool _busy, _building, _dirty, _initialized;
    private int _editIndex;
    public event EventHandler? MinimizeRequested, ExitConfirmed;
    private bool ServiceMode => RunMode.SelectedIndex == 0;
    private string Pipe => ServiceMode ? ControlProtocol.ServicePipe(_serviceName) : ControlProtocol.DaemonPipe(_path);

    private Task<ControlResponse> SendControlAsync(ControlRequest request, CancellationToken token) =>
        ControlProtocol.SendAsync(Pipe, request, token, ServiceMode ? _service.ProcessId :
            _foregroundTask is { IsCompleted: false } ? Environment.ProcessId : null);

    public MainPage()
    {
        InitializeComponent();
        _building = true;
        RunMode.ItemsSource = new[] { "Windows service", "App daemon" }; RunMode.SelectedIndex = 0;
        ThemePicker.ItemsSource = new[] { "System", "Light", "Dark" }; ThemePicker.SelectedItem = Preferences.Default.Get("theme", "System");
        FirewallProfiles.ItemsSource = new[] { "domain,private", "domain", "private", "public", "any" }; FirewallProfiles.SelectedIndex = 0;
        _path = Preferences.Default.Get("configPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tedd.TcpTunnel", "tunnel.json"));
        ConfigPath.Text = _path;
        ConfigPath.TextChanged += (_, e) => { if (e.NewTextValue != _path) MarkDirty(); };
        DaemonPath.Text = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "tcptunnel.exe");
        OutboundContainer.Add(_outbound); InboundContainer.Add(_inbound);
        _building = false;
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
#if CONTROL_PANEL_SMOKE
            var arguments = Environment.GetCommandLineArgs();
            var smoke = Array.IndexOf(arguments, "--smoke-test");
            if (smoke >= 0 && smoke + 1 < arguments.Length) { await RunSmokeAsync(arguments[smoke + 1]); return; }
#endif
            await ActionAsync(async () => { await RefreshAsync(); LoadLocal(); });
            _timer = Dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => { if (!_busy && _pollTask.IsCompleted) _pollTask = PollAsync(); };
            _timer.Start();
        };
    }

    private async Task ActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; Content.IsEnabled = false;
        try { await _pollTask; await action(); }
        catch (Exception ex) { Message.Text = ex.Message; await DisplayAlertAsync("Action failed", ex.Message, "OK"); }
        finally { _busy = false; Content.IsEnabled = true; }
    }

    private async Task PollAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { SetUnavailable(ex.Message); }
    }

    private async Task RefreshAsync()
    {
        _service = await Task.Run(() => WindowsOperations.ReadService(_serviceName));
        StartupStatus.Text = _service.Installed ? (_service.Automatic ? "Automatic Windows service" : "Service starts manually") : "Service not installed";
        StartupButton.Text = _service.Automatic ? "Disable Windows startup" : "Enable Windows startup";
        if (ServiceMode && _service.ConfigPath is { } path)
        {
            if (_path != path && _dirty) throw new InvalidOperationException("Save or reload your edits before connecting to this service.");
            _path = path; ConfigPath.Text = path; DaemonPath.Text = _service.Executable;
        }
        if (ServiceMode && _service.State != "Running") { SetUnavailable("Windows service · " + _service.State); return; }
        try
        {
            var response = await SendControlAsync(new("status"), _shutdown.Token);
            _status = response.Status ?? throw new InvalidDataException("The daemon returned no status.");
            if (_status.IsService != ServiceMode || !string.Equals(Path.GetFullPath(_status.ConfigPath), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The daemon does not match the selected runtime and configuration.");
            _history.Add(_status);
            RuntimeStatus.Text = ServiceMode ? "Windows service · Running" : "App daemon · Running";
            RuntimeDetail.Text = $"{_serviceName}   ·   PID {_status.ProcessId}   ·   Uptime {TimeSpan.FromSeconds(_status.SampleSeconds):hh\\:mm\\:ss}";
            PendingLabel.Text = _status.PendingConfiguration ? "Saved configuration is pending. Restart the service or app daemon to apply it." : "Running configuration matches the saved file.";
            var connections = _status.Forwards.Sum(f => f.ActiveConnections); ConnectionCount.Text = connections + (connections == 1 ? " connection" : " connections");
            var names = new[] { TelemetryHistory.AllForwards }.Concat(_status.Forwards.Select(f => f.Name)).ToArray();
            var old = TrafficForward.SelectedItem as string;
            if (TrafficForward.ItemsSource is not string[] previous || !previous.SequenceEqual(names))
            {
                TrafficForward.ItemsSource = names;
                TrafficForward.SelectedItem = names.Contains(old) ? old : names.FirstOrDefault();
            }
            ListenerList.Clear();
            foreach (var forward in _status.Forwards)
                ListenerList.Add(new Label { Text = $"{forward.Name}   ·   {forward.Mode}   ·   {forward.ListenEndpoint} → {forward.RemoteEndpoint}   ·   {forward.ActiveConnections} active", FontSize = 12 });
            UpdateCharts();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            SetUnavailable(ServiceMode ? "Service running · control channel unavailable: " + ex.Message :
                _foregroundTask is { IsCompleted: true } ? "App daemon stopped · check configuration and listener ports." : "App daemon · Stopped or unavailable");
        }
    }

    private void SetUnavailable(string reason)
    {
        _status = null; _history.Reset();
        RuntimeStatus.Text = reason; RuntimeDetail.Text = _serviceName; ConnectionCount.Text = "— connections";
        PendingLabel.Text = "Telemetry unavailable. Start the selected runtime or check its configuration.";
        ListenerList.Clear(); UpdateCharts();
    }

    private void UpdateCharts()
    {
        var samples = _history.For(TrafficForward.SelectedItem as string ?? "");
        _outbound.Update(samples); _inbound.Update(samples);
    }

    private void LoadLocal()
    {
        _document = File.Exists(_path) ? ConfigurationFile.Read(_path) : null;
        _draft = _document is null ? new() { Forwards = [new()] } : ConfigurationFile.Parse(_document.Json);
        ConfigPath.Text = _path; _dirty = false; BuildForms();
        Message.Text = _document is null ? "Configure a forward and save before starting." : "Loaded " + _path;
    }

    private void BuildForms()
    {
        _building = true;
        _editIndex = Math.Clamp(_editIndex, 0, Math.Max(0, _draft.Forwards.Count - 1));
        EditForward.ItemsSource = _draft.Forwards.Select(f => f.Name).ToArray();
        EditForward.SelectedIndex = _draft.Forwards.Count == 0 ? -1 : _editIndex;
        BuildForwardForm();
        _applicationForm = new OptionForm(new ApplicationSettings { Logging = _draft.Logging, Update = _draft.Update });
        ApplicationFormHost.Content = _applicationForm;
        WatchEdits(_applicationForm);
        _building = false;
    }

    private void BuildForwardForm()
    {
        _forwardForm = _draft.Forwards.Count == 0 ? null : new OptionForm(_draft.Forwards[_editIndex]);
        ForwardFormHost.Content = _forwardForm;
        if (_forwardForm is not null) WatchEdits(_forwardForm);
    }

    private void WatchEdits(IVisualTreeElement element)
    {
        if (element is Entry entry) entry.TextChanged += (_, _) => MarkDirty();
        if (element is Editor editor) editor.TextChanged += (_, _) => MarkDirty();
        if (element is Switch toggle) toggle.Toggled += (_, _) => MarkDirty();
        if (element is Picker picker) picker.SelectedIndexChanged += (_, _) => MarkDirty();
        foreach (var child in element.GetVisualChildren()) WatchEdits(child);
    }

    private void MarkDirty() { if (!_building) { _dirty = true; Message.Text = "Unsaved configuration changes"; } }
    private void ApplyForms() { _forwardForm?.Apply(); _applicationForm?.Apply(); }

    private async Task SaveAsync(bool askRestart)
    {
        ApplyForms(); _draft.Validate();
        if (!ServiceMode && _foregroundTask is null || ServiceMode && !_service.Installed)
        {
            var newPath = Path.GetFullPath(ConfigPath.Text);
            if (newPath != _path) { _document = null; _path = newPath; }
        }
        var json = JsonSerializer.Serialize(_draft, ConfigurationFile.Json);
        if (_status is not null)
            _document = (await SendControlAsync(new("save", json, _document?.Revision), _shutdown.Token)).Configuration;
        else _document = ConfigurationFile.Save(_path, json, _document?.Revision);
        _dirty = false; Preferences.Default.Set("configPath", _path); ConfigPath.Text = _path;
        Message.Text = "Saved " + _path;
        await RefreshAsync();
        if (!askRestart || _status is null) return;
        var choice = await DisplayActionSheetAsync("Configuration saved. Apply it now?", "Later", null,
            ServiceMode ? "Restart service" : "Restart app daemon", "Restart connections only");
        if (choice is "Restart service" or "Restart app daemon") await RestartAsync();
        else if (choice == "Restart connections only" &&
            await DisplayAlertAsync("Restart connections only", "This disconnects existing sessions using the current settings. Saved configuration remains pending until the runtime restarts.", "Disconnect sessions", "Cancel"))
            await SendControlAsync(new("restart-connections"), _shutdown.Token);
    }

    private sealed class ApplicationSettings
    {
        public LoggingOptions Logging { get; set; } = new();
        public UpdateOptions Update { get; set; } = new();
    }
}
