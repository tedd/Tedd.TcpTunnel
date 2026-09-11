using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tedd.TcpTunnel.Console;

internal sealed record Command(TunnelOptions Options, bool Help, bool Version, bool Check, string? WriteConfig, bool CheckUpdate, bool UpdateNow, bool Yes);

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
        bool help = false, version = false, check = false, checkUpdate = false, update = false, yes = false;
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
                case "help": help = true; break;
                case "version": version = true; break;
                case "check": check = true; break;
                case "check-update": checkUpdate = true; break;
                case "update-now": update = true; break;
                case "yes": yes = true; break;
                case "config": config = Value(); break;
                case "write-config": write = Value(); break;
                default: overrides.Add((key, Value())); break;
            }
        }
        var options = config is null ? new TunnelOptions() : JsonSerializer.Deserialize<TunnelOptions>(File.ReadAllText(config), Json)
            ?? throw new ArgumentException("Configuration cannot be null.");
        if (options.Forwards is null || options.Forwards.Any(f => f is null)) throw new ArgumentException("Forwards must be an array of objects.");
        var node = JsonSerializer.SerializeToNode(options, Json)!.AsObject();
        var forwards = node[nameof(TunnelOptions.Forwards)]!.AsArray();
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
            if (Normalize(path[0]) is not ("forwards" or "update"))
            {
                if (forwards.Count == 0) forwards.Add(JsonSerializer.SerializeToNode(new ForwardOptions(), Json));
                root = forwards[current]!;
            }
            Set(root, path, value);
        }
        options = node.Deserialize<TunnelOptions>(Json)!;
        if (!help && !version && !checkUpdate && !update)
        {
            if (write is not null && options.Forwards.Count == 0) options.Forwards.Add(new());
            options.Validate();
        }
        else if (options.Update is null) throw new ArgumentException("Update cannot be null.");
        else options.Update.Validate();
        return new(options, help, version, check, write, checkUpdate, update, yes);
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
            var property = obj.FirstOrDefault(p => Normalize(p.Key) == Normalize(path[i]));
            if (property.Key is null) throw new ArgumentException($"Unknown option: {string.Join(':', path)}");
            if (i + 1 < path.Length) { root = property.Value ?? throw new ArgumentException($"Option {path[i]} is not an object."); continue; }
            if (property.Value is JsonObject or JsonArray) throw new ArgumentException("Set individual option fields.");
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
Tedd.TcpTunnel — TCP forwarding and compression

tcptunnel --config tunnel.json [overrides]
tcptunnel --forward web --mode Raw --listen-port 8080 --remote-host example.org --remote-port 80
tcptunnel --forward compressed --mode Client --listen-port 9000 --remote-port 9001 --compression Brotli

--forward NAME selects or adds a forward; subsequent short options apply to it.
Every JSON field can be set using --forwards:0:socket:no-delay false or --socket:no-delay=false.
Hyphenated, PascalCase and camelCase field names are equivalent. CLI values override JSON.
Booleans accept true/false; a flag without a value means true. Use null to clear optional strings.

--config PATH          Load JSON configuration
--write-config PATH    Write the complete effective configuration and exit
--check                Validate configuration without opening listeners
--check-update         Check GitHub Releases and exit
--update-now [--yes]   Download, verify and offer to apply an update
--help                 Show usage and every configuration option
--version              Show version

Full option template (defaults):
""" + "\n" + JsonSerializer.Serialize(new TunnelOptions { Forwards = [new()] }, Json);
}
