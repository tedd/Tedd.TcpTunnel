using System.Net;

namespace Tedd.TcpTunnel.Management;

public sealed record FirewallRule(string Name, string LocalAddress, int Port, string Program, string Profiles, string RemoteAddresses)
{
    public IReadOnlyList<string> AddArguments => ["advfirewall", "firewall", "add", "rule", "name=" + Name,
        "dir=in", "action=allow", "protocol=TCP", "localport=" + Port, "localip=" + LocalAddress,
        "program=" + Program, "profile=" + Profiles, "remoteip=" + RemoteAddresses, "enable=yes"];
    public IReadOnlyList<string> DeleteArguments => ["advfirewall", "firewall", "delete", "rule", "name=" + Name, "program=" + Program];
}

public static class FirewallPlan
{
    public static IReadOnlyList<FirewallRule> Build(TunnelOptions options, string serviceName, string executable,
        string profiles = "domain,private", string remoteAddresses = "LocalSubnet")
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Invalid service name.");
        if (profiles is not ("domain,private" or "domain" or "private" or "public" or "any")) throw new ArgumentException("Invalid firewall profiles.");
        if (remoteAddresses is not ("LocalSubnet" or "any"))
        {
            var acl = new ForwardOptions { AccessControl = new() { Allow = remoteAddresses.Split(',', StringSplitOptions.TrimEntries).ToList() } };
            acl.Validate();
        }
        return options.Forwards.Where(f => f.ListenPort != 0 && !IPAddress.IsLoopback(IPAddress.Parse(f.ListenAddress)))
            .Select(f => new FirewallRule("Tedd.TcpTunnel." + serviceName + "." + f.Name,
                f.ListenAddress is "0.0.0.0" or "::" ? "any" : f.ListenAddress, f.ListenPort,
                Path.GetFullPath(executable), profiles, remoteAddresses)).ToArray();
    }
}
