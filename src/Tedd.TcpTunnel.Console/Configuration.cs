using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tedd.TcpTunnel.Console;

internal enum ServiceOperation { None, Run, Install, Uninstall }

internal sealed record Command(TunnelOptions Options, bool Help, bool Version, bool Check, string? WriteConfig,
    bool CheckUpdate, bool UpdateNow, bool Yes, bool GenerateKey, string? ConfigPath, ServiceOperation Service,
    string ServiceName);

internal static class Configuration
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static Command Parse(string[] args)
    {
        string? config = null, write = null;
        var serviceName = "Tedd.TcpTunnel";
        var service = ServiceOperation.None;
        bool help = false, version = false, check = false, checkUpdate = false, update = false, yes = false, generateKey = false;
        var overrides = new List<(string Key, string Value)>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-h") arg = "--help";
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected an option, found: {arg}");
            var split = arg[2..].Split('=', 2);
            var key = split[0];
            string Value() => split.Length == 2 ? split[1] : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            switch (key)
            {
                case "generate-key": generateKey = true; break;
                case "debug": overrides.Add(("logging:level", "Debug")); break;
                case "log-file": overrides.Add(("logging:file", Value())); break;
                case "help": help = true; break;
                case "version": version = true; break;
                case "check": check = true; break;
                case "check-update": checkUpdate = true; break;
                case "update-now": update = true; break;
                case "yes": yes = true; break;
                case "service": SetService(ServiceOperation.Run); break;
                case "install-service": SetService(ServiceOperation.Install); break;
                case "uninstall-service": SetService(ServiceOperation.Uninstall); break;
                case "service-name": serviceName = Value(); break;
                case "config": config = Value(); break;
                case "write-config": write = Value(); break;
                default: overrides.Add((key, Value())); break;
            }
        }
        void SetService(ServiceOperation operation)
        {
            if (service != ServiceOperation.None) throw new ArgumentException("Specify only one service operation.");
            service = operation;
        }
        if (serviceName.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(serviceName[0]) ||
            serviceName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("ServiceName must contain 1–64 ASCII letters, digits, periods, hyphens or underscores and start with a letter or digit.");
        if (!help && service is (ServiceOperation.Run or ServiceOperation.Install) && config is null)
            throw new ArgumentException("--config PATH is required when running or installing a service.");
        if (service == ServiceOperation.Install && overrides.Count != 0)
            throw new ArgumentException("Put service configuration in the JSON file instead of using command-line overrides during installation.");
        var options = config is null ? new TunnelOptions() : JsonSerializer.Deserialize<TunnelOptions>(File.ReadAllText(config), Json)
            ?? throw new ArgumentException("Configuration cannot be null.");
        if (options.Forwards is null || options.Forwards.Any(f => f is null)) throw new ArgumentException("Forwards must be an array of objects.");
        var node = JsonSerializer.SerializeToNode(options, Json)!.AsObject();
        var forwards = node[nameof(TunnelOptions.Forwards)]!.AsArray();
        for (var j = 0; j < options.Forwards.Count; j++)
            if (options.Forwards[j].Encryption?.Keys is { } keys)
            {
                var map = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = false });
                foreach (var pair in keys) map.Add(pair.Key, JsonValue.Create(pair.Value));
                forwards[j]![nameof(ForwardOptions.Encryption)]![nameof(EncryptionOptions.Keys)] = map;
            }
        var current = 0;
        foreach (var (key, value) in overrides)
        {
            if (key == "forward")
            {
                current = -1;
                for (var j = 0; j < forwards.Count; j++)
                    if (string.Equals(forwards[j]![nameof(ForwardOptions.Name)]!.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase)) current = j;
                if (current < 0) { current = forwards.Count; forwards.Add(JsonSerializer.SerializeToNode(new ForwardOptions { Name = value }, Json)); }
                continue;
            }
            var path = key.Replace('.', ':').Split(':');
            JsonNode root = node;
            if (Normalize(path[0]) is not ("forwards" or "update" or "logging"))
            {
                if (forwards.Count == 0) forwards.Add(JsonSerializer.SerializeToNode(new ForwardOptions(), Json));
                root = forwards[current]!;
            }
            Set(root, path, value);
        }
        options = node.Deserialize<TunnelOptions>(Json)!;
        if (!help && !version && !checkUpdate && !update && !generateKey && service != ServiceOperation.Uninstall)
        {
            if (write is not null && options.Forwards.Count == 0) options.Forwards.Add(new());
            options.Validate();
        }
        else if (options.Update is null) throw new ArgumentException("Update cannot be null.");
        else options.Update.Validate();
        return new(options, help, version, check, write, checkUpdate, update, yes, generateKey,
            config is null ? null : Path.GetFullPath(config), service, serviceName);
    }

    private static string Normalize(string key) => key.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    private static void Set(JsonNode root, string[] path, string value)
    {
        for (var i = 0; i < path.Length; i++)
        {
            if (root is JsonArray array)
            {
                if (!int.TryParse(path[i], out var index) || index < 0 || index >= array.Count || i == path.Length - 1) throw new ArgumentException($"Unknown forward index: {path[i]}");
                root = array[index]!; continue;
            }
            if (root is not JsonObject obj) throw new ArgumentException($"Invalid option path: {string.Join(':', path)}");
            if (i > 0 && Normalize(path[i - 1]) == "keys" && i == path.Length - 1)
            {
                obj[path[i]] = value;
                return;
            }
            var property = obj.FirstOrDefault(p => Normalize(p.Key) == Normalize(path[i]));
            if (property.Key is null) throw new ArgumentException($"Unknown option: {string.Join(':', path)}");
            if (i + 1 < path.Length && Normalize(property.Key) == "keys" && property.Value is null)
                obj[property.Key] = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = false });
            if (i + 1 < path.Length) { root = obj[property.Key] ?? throw new ArgumentException($"Option {path[i]} is not an object."); continue; }
            if (property.Value is JsonArray values)
            {
                if (value == "null") values.Clear();
                else values.Add(value);
                return;
            }
            if (property.Value is JsonObject) throw new ArgumentException("Set individual option fields.");
            var kind = property.Value?.GetValueKind();
            obj[property.Key] = kind switch
            {
                JsonValueKind.True or JsonValueKind.False => bool.TryParse(value, out var boolean) ? JsonValue.Create(boolean) : throw new ArgumentException($"Expected true/false for {path[i]}"),
                JsonValueKind.Number => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : throw new ArgumentException($"Expected an integer for {path[i]}"),
                _ => value == "null" ? null : JsonValue.Create(value)
            };
        }
    }

    public static string HelpText => """
Tedd.TcpTunnel — TCP forwarding, compression and authenticated encryption

tcptunnel --config tunnel.json [overrides]
tcptunnel --forward web --mode Raw --listen-port 8080 --remote-host example.org --remote-port 80
tcptunnel --forward compressed --mode Client --listen-port 9000 --remote-port 9001 --compression Brotli

--forward NAME selects or adds a forward; subsequent short options apply to it.
Encryption: --encryption:algorithm ChaCha20Poly1305
Client: --encryption:key-id laptop --encryption:key BASE64
Server: --encryption:keys:laptop BASE64 (repeat with other IDs for other clients)
Keep production keys in a restricted configuration file to avoid shell history/process exposure.
Every JSON field can be set using --forwards:0:socket:no-delay false or --socket:no-delay=false.
Hyphenated, PascalCase and camelCase field names are equivalent. CLI values override JSON.
Booleans accept true/false; a flag without a value means true. Use null to clear optional strings.

--config PATH          Load JSON configuration
--write-config PATH    Write the complete effective configuration and exit
--generate-key         Generate a random 32-byte Base64 shared key and exit
--debug                Include debug events in the log
--log-file PATH        Append JSON-line logs to a file
--check                Validate configuration without opening listeners
--install-service      Install and start a Windows or systemd service; requires --config
--uninstall-service    Stop and uninstall the service
--service-name NAME    Select a service instance (default: Tedd.TcpTunnel)
--check-update         Check GitHub Releases and exit
--update-now [--yes]   Download, verify and offer to apply an update
--help                 Show usage and every configuration option
--version              Show version

Full option template (defaults):
""" + "\n" + JsonSerializer.Serialize(new TunnelOptions { Forwards = [new()] }, Json);
}
