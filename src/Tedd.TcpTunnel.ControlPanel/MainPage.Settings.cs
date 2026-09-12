using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.ControlPanel;

public partial class MainPage
{
    private void ShowPage(string page)
    {
        ApplyForms();
        OverviewContent.IsVisible = page == "Overview"; ForwardsContent.IsVisible = page == "Forwards";
        ApplicationContent.IsVisible = page == "Application"; FirewallContent.IsVisible = page == "Firewall";
        PageTitle.Text = page == "Overview" ? "Tunnel overview" : page;
        PageSubtitle.Text = page switch
        {
            "Forwards" => "Endpoints, compression, encryption, access control, and socket behavior.",
            "Application" => "Configuration, logging, updates, and Windows service installation.",
            "Firewall" => "Review and manage scoped inbound TCP rules.",
            _ => "Live connections, payload throughput, and compression."
        };
    }
    private void OnOverview(object? sender, EventArgs e) => _ = ActionAsync(() => { ShowPage("Overview"); return Task.CompletedTask; });
    private void OnForwards(object? sender, EventArgs e) => _ = ActionAsync(() => { ShowPage("Forwards"); return Task.CompletedTask; });
    private void OnApplication(object? sender, EventArgs e) => _ = ActionAsync(() => { ShowPage("Application"); return Task.CompletedTask; });
    private void OnFirewall(object? sender, EventArgs e) => _ = ActionAsync(() => { ShowPage("Firewall"); return Task.CompletedTask; });
    private void OnSave(object? sender, EventArgs e) => _ = ActionAsync(() => SaveAsync(true));
    private void OnReload(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (!_dirty || await DisplayAlertAsync("Reload configuration", "Discard unsaved edits?", "Reload", "Cancel"))
        { _dirty = false; await RefreshAsync(); LoadLocal(); }
    });
    private void OnEditForwardChanged(object? sender, EventArgs e)
    {
        if (_building || EditForward.SelectedIndex < 0) return;
        _ = ActionAsync(() =>
        {
            _forwardForm?.Apply(); _editIndex = EditForward.SelectedIndex; BuildForwardForm(); return Task.CompletedTask;
        });
    }
    private void OnAddForward(object? sender, EventArgs e) => _ = ActionAsync(() =>
    {
        ApplyForms(); var number = 1;
        while (_draft.Forwards.Any(f => f.Name == "forward" + number)) number++;
        _draft.Forwards.Add(new() { Name = "forward" + number }); _editIndex = _draft.Forwards.Count - 1;
        BuildForms(); MarkDirty(); return Task.CompletedTask;
    });
    private void OnRemoveForward(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (_draft.Forwards.Count <= 1) throw new InvalidOperationException("At least one forward is required.");
        if (await DisplayAlertAsync("Remove forward", "Remove " + _draft.Forwards[_editIndex].Name + " from the configuration?", "Remove", "Cancel"))
        { _draft.Forwards.RemoveAt(_editIndex); BuildForms(); MarkDirty(); }
    });
    private void OnConnectService(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        WindowsOperations.ValidateName(ServiceName.Text);
        if (_dirty && !await DisplayAlertAsync("Connect to service", "Discard unsaved edits?", "Connect", "Cancel")) return;
        _dirty = false; _serviceName = ServiceName.Text;
        _building = true; RunMode.SelectedIndex = 0; _building = false;
        _status = null; _history.Reset(); await RefreshAsync(); LoadLocal();
    });
    private void OnOpenConfig(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        if (_foregroundTask is { IsCompleted: false }) throw new InvalidOperationException("Stop the app daemon before opening another configuration.");
        if (_dirty && !await DisplayAlertAsync("Open configuration", "Discard unsaved edits?", "Open", "Cancel")) return;
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Open TcpTunnel JSON configuration" });
        if (file is null) return;
        _ = ConfigurationFile.Parse(await File.ReadAllTextAsync(file.FullPath));
        _path = file.FullPath;
        _building = true; RunMode.SelectedIndex = 1; _building = false;
        _status = null; _history.Reset(); LoadLocal(); await RefreshAsync();
    });

    private IReadOnlyList<FirewallRule> PreviewFirewall()
    {
        ApplyForms();
        var executable = ServiceMode ? DaemonPath.Text : Environment.ProcessPath!;
        var plan = FirewallPlan.Build(_draft, _serviceName + (ServiceMode ? ".Service" : ".App"), executable, (string)FirewallProfiles.SelectedItem, FirewallPeers.Text);
        FirewallPreview.Text = plan.Count == 0 ? "No non-loopback listeners with fixed ports." :
            string.Join("\n\n", plan.Select(r => $"{r.Name}\nTCP {r.LocalAddress}:{r.Port}   Profiles: {r.Profiles}\nRemote: {r.RemoteAddresses}\nProgram: {r.Program}"));
        if (_dirty) FirewallPreview.Text += "\n\nThese rules reflect unsaved edits. Save and restart to activate the matching listeners.";
        return plan;
    }
    private void OnPreviewFirewall(object? sender, EventArgs e) => _ = ActionAsync(() => { PreviewFirewall(); return Task.CompletedTask; });
    private void OnApplyFirewall(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        var rules = PreviewFirewall();
        if (rules.Count == 0 || !await DisplayAlertAsync("Apply firewall rules", FirewallPreview.Text, "Apply", "Cancel")) return;
        foreach (var rule in rules) await WindowsOperations.ApplyFirewallRuleAsync(rule);
        Message.Text = "Firewall rules applied.";
    });
    private void OnRemoveFirewall(object? sender, EventArgs e) => _ = ActionAsync(async () =>
    {
        var rules = PreviewFirewall();
        if (rules.Count == 0 || !await DisplayAlertAsync("Remove firewall rules", FirewallPreview.Text, "Remove", "Cancel")) return;
        foreach (var rule in rules) await WindowsOperations.RunAsync(WindowsOperations.SystemTool("netsh.exe"), rule.DeleteArguments.ToArray());
        Message.Text = "Matching TcpTunnel firewall rules removed.";
    });
    private void OnOpenFirewall(object? sender, EventArgs e) => _ = ActionAsync(() =>
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WindowsOperations.SystemTool("control.exe"))
            { UseShellExecute = true, Arguments = "/name Microsoft.WindowsFirewall" });
        return Task.CompletedTask;
    });
}
