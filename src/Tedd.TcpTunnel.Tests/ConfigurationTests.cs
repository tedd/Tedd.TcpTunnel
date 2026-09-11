using System.Reflection;
using System.Text.Json;
using Tedd.TcpTunnel.Console;

namespace Tedd.TcpTunnel.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void EveryLeafOptionHasACommandLineEquivalent()
    {
        var options = new TunnelOptions { Forwards = [new()] };
        var args = new List<string> { "--forward", "default" };
        void Add(object value, string prefix)
        {
            foreach (var property in value.GetType().GetProperties())
            {
                var item = property.GetValue(value); var key = prefix + property.Name;
                if (item is not null && property.PropertyType.IsClass && property.PropertyType != typeof(string)) { Add(item, key + ":"); continue; }
                args.Add("--" + key); args.Add(item?.ToString() ?? "null");
            }
        }
        Add(options.Forwards[0], ""); Add(options.Update, "update:");
        var parsed = Configuration.Parse(args.ToArray());
        Assert.Equal(JsonSerializer.Serialize(options, Configuration.Json), JsonSerializer.Serialize(parsed.Options, Configuration.Json));
    }

    [Fact]
    public void NamedForwardsAndIndexedOverridesAreDeterministic()
    {
        var command = Configuration.Parse(["--forward", "a", "--listen-port=1", "--forward=b", "--listen-port", "2", "--forward", "a", "--socket:no-delay", "false", "--forwards:1:remote-port", "443"]);
        Assert.Equal(2, command.Options.Forwards.Count);
        Assert.Equal(1, command.Options.Forwards[0].ListenPort);
        Assert.False(command.Options.Forwards[0].Socket.NoDelay);
        Assert.Equal(443, command.Options.Forwards[1].RemotePort);
    }

    [Fact]
    public void JsonDefaultsAreOverriddenAndRoundTrip()
    {
        using var directory = new TempDirectory(); var file = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(file, """{"Forwards":[{"Name":"web","ListenPort":8088}],"Update":{"CheckOnStartup":false}}""");
        var command = Configuration.Parse(["--config", file, "--ListenPort", "9090", "--update:include-prerelease"]);
        Assert.Equal(9090, command.Options.Forwards[0].ListenPort); Assert.False(command.Options.Update.CheckOnStartup); Assert.True(command.Options.Update.IncludePrerelease);
        command.Options.Validate();
    }

    [Theory]
    [InlineData("--unknown")] [InlineData("positional")] [InlineData("--socket:no-delay=wrong")]
    [InlineData("--listen-port=x")] [InlineData("--forwards:9:name=x")]
    [InlineData("--forwards:0=x")] [InlineData("--socket=x")]
    [InlineData("--mode=invalid")] [InlineData("--compression=5")]
    [InlineData("--socket:linux-congestion-control:bad=x")]
    public void InvalidCliIsRejected(string arg) => Assert.ThrowsAny<Exception>(() => Configuration.Parse(["--forward=test", arg]));

    [Theory]
    [InlineData("null")] [InlineData("{\"Typo\":true}")] [InlineData("{\"Forwards\":null}")]
    [InlineData("{\"Forwards\":[null]}")] [InlineData("{\"Forwards\":[{}],\"Update\":null}")]
    public void InvalidJsonIsRejected(string json)
    {
        using var directory = new TempDirectory(); var file = Path.Combine(directory.Path, "config.json"); File.WriteAllText(file, json);
        Assert.ThrowsAny<Exception>(() => Configuration.Parse(["--config", file]));
    }

    [Theory]
    [InlineData("--help")] [InlineData("-h")] [InlineData("--version")] [InlineData("--check-update")]
    [InlineData("--update-now")]
    public void InformationalCommandsDoNotRequireForwards(string option) => Assert.NotNull(Configuration.Parse([option]));

    [Fact]
    public async Task ConsoleCommandsReturnUsefulExitCodes()
    {
        Assert.Equal(0, await Program.Main(["--version"]));
        Assert.Equal(0, await Program.Main(["--help"]));
        Assert.Equal(0, await Program.Main(["--check", "--listen-port=1000"]));
        Assert.Equal(1, await Program.Main(["--bogus"]));
        using var directory = new TempDirectory(); var file = Path.Combine(directory.Path, "written.json");
        Assert.Equal(0, await Program.Main(["--write-config", file]));
        Configuration.Parse(["--config", file]).Options.Validate();
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        foreach (var name in new[] { "BufferSize", "ListenPort", "RemotePort", "BatchMilliseconds", "HeartbeatMilliseconds", "IdleTimeoutMilliseconds", "HandshakeTimeoutMilliseconds", "MaxConnections", "Backlog", "BrotliQuality", "BrotliWindow", "ZstandardLevel" })
        { yield return [name, int.MinValue]; yield return [name, int.MaxValue]; }
        foreach (var value in new[] { "", "a/b", "with space", new string('a', 101) }) yield return ["Name", value];
        yield return ["ListenAddress", "localhost"];
        yield return ["RemoteHost", ""];
        yield return ["Mode", (TunnelMode)999]; yield return ["Compression", (Codec)999]; yield return ["Execution", (ExecutionMode)999];
        yield return ["CompressionLevel", (System.IO.Compression.CompressionLevel)999];
    }

    [Theory, MemberData(nameof(InvalidOptions))]
    public void InvalidForwardOptionsFailBeforeBinding(string property, object value)
    {
        // int.MaxValue is intentionally a valid unlimited socket/idle timeout bound.
        if (property == "IdleTimeoutMilliseconds" && value.Equals(int.MaxValue)) return;
        var options = new ForwardOptions(); typeof(ForwardOptions).GetProperty(property)!.SetValue(options, value);
        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    [Fact]
    public void CrossFieldAndNestedValidation()
    {
        Assert.Throws<ArgumentException>(() => new TunnelOptions().Validate());
        Assert.Throws<ArgumentException>(() => new TunnelOptions { Forwards = [new(), new()] }.Validate());
        Assert.Throws<ArgumentException>(() => new ForwardOptions { CompressionHistory = true }.Validate());
        Assert.Throws<ArgumentException>(() => new ForwardOptions { Compression = Codec.Brotli }.Validate());
        Assert.Throws<ArgumentException>(() => new ForwardOptions { Mode = TunnelMode.Socks5, ListenAddress = "0.0.0.0" }.Validate());
        new ForwardOptions { Mode = TunnelMode.Socks5, ListenAddress = "0.0.0.0", AllowRemoteSocks = true }.Validate();
        Assert.Throws<ArgumentException>(() => new ForwardOptions { Retry = null! }.Validate());
        Assert.Throws<ArgumentException>(() => new TunnelOptions { Forwards = [new()], Update = null! }.Validate());
        foreach (var type in new[] { typeof(RetryOptions), typeof(SocketOptions), typeof(CaptureOptions), typeof(UpdateOptions) })
        foreach (var property in type.GetProperties().Where(p => p.PropertyType == typeof(int)))
        {
            var instance = Activator.CreateInstance(type)!; property.SetValue(instance, -1);
            var method = type.GetMethod("Validate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(instance, null));
            Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryOptions { MaxDelayMilliseconds = 1 }.Validate());
        Assert.Throws<ArgumentException>(() => new SocketOptions { LinuxCongestionControl = "bad name" }.Validate());
        Assert.Throws<ArgumentException>(() => new CaptureOptions { Directory = " " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureOptions { MaxFileBytes = 1 }.Validate());
        Assert.Throws<ArgumentException>(() => new UpdateOptions { Repository = "https://example.org" }.Validate());
        Assert.Throws<ArgumentException>(() => new UpdateOptions { InstallKind = "unknown" }.Validate());
    }

    [Fact]
    public void RetryBackoffIsBoundedAndJittered()
    {
        var retry = new RetryOptions { InitialDelayMilliseconds = 100, MaxDelayMilliseconds = 400 };
        Assert.Equal(50, retry.Delay(0, 0).TotalMilliseconds);
        Assert.Equal(100, retry.Delay(0, 1).TotalMilliseconds);
        Assert.Equal(400, retry.Delay(99, 1).TotalMilliseconds);
        retry.Jitter = false; Assert.Equal(200, retry.Delay(1, 0).TotalMilliseconds);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TcpTunnel-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
