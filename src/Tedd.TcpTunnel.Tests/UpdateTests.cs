using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Tedd.TcpTunnel.Console;

namespace Tedd.TcpTunnel.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExplicitCliUpdateStagesVerifiedBinaryAndReportsLaunchFailure(bool launchFails)
    {
        using var directory = new TempDirectory();
        var executable = OperatingSystem.IsWindows() ? "tcptunnel.exe" : "tcptunnel";
        var target = Path.Combine(directory.Path, executable); File.WriteAllText(target, "original");
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, true))
        { using var entry = archive.CreateEntry(executable).Open(); entry.Write("updated"u8); }
        var bytes = package.ToArray(); var available = Available(bytes, out var sums);
        var release = new GitHubRelease("v2.2.0", "", false, false, available.Assets);
        var launched = false;
        var runtime = new UpdateRuntime(directory.Path, target, true, directory.Path, _ =>
        { launched = true; if (launchFails) throw new System.ComponentModel.Win32Exception("Launch denied."); });
        var services = new ApplicationServices(() => Http(url => url.Contains("api.github.com", StringComparison.Ordinal)
            ? JsonSerializer.SerializeToUtf8Bytes(new[] { release }) : url.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? sums : bytes),
            () => throw new Exception("--yes must not prompt."), runtime);
        Assert.Equal(launchFails ? 1 : 0, await Program.RunAsync(["--update-now", "--yes"], services, TestContext.Current.CancellationToken));
        Assert.True(launched); Assert.Equal("original", File.ReadAllText(target));
    }
    [Theory]
    [InlineData("y", true)] [InlineData("Y", true)] [InlineData("yes", false)] [InlineData("", false)]
    public void UpdatePromptRequiresExplicitConsent(string response, bool expected)
    {
        using var input = new StringReader(response); using var output = new StringWriter();
        Assert.Equal(expected, Program.ConfirmUpdate(input, output)); Assert.Contains("[y/N]", output.ToString());
    }
    [Fact]
    public async Task CliUpdateChecksAndDeclinesAreNonMutating()
    {
        var version = "2.2.0";
        var release = new GitHubRelease("v" + version, "", false, false,
            [Asset(ReleaseClient.AssetName(version, ReleaseClient.RuntimeId(), "zip"), [1]), Asset("SHA256SUMS", [1])]);
        var services = new ApplicationServices(() => Http(_ => JsonSerializer.SerializeToUtf8Bytes(new[] { release })), () => false);
        Assert.Equal(0, await Program.RunAsync(["--check-update"], services, TestContext.Current.CancellationToken));
        Assert.Equal(0, await Program.RunAsync(["--update-now"], services, TestContext.Current.CancellationToken));
        Assert.Equal(0, await Program.RunAsync(["--check-update"], services with { CreateHttpClient = () => Http(_ => "[]"u8.ToArray()) }, TestContext.Current.CancellationToken));
        Assert.Equal(1, await Program.RunAsync(["--update-now", "--yes"], services, TestContext.Current.CancellationToken));
        Assert.False(UpdateRuntime.Current.SingleFile);
    }

    [Fact]
    public async Task ConsoleHostCancelsAndStopsUpdater()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var services = new ApplicationServices(() => Http(_ => "[]"u8.ToArray()), () => false);
        Assert.Equal(0, await Program.RunAsync(["--listen-port", "0", "--update:check-on-startup", "false"], services, stop.Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(0, await Program.RunAsync(["--write-config", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], services, cancelled.Token));
    }
    [Theory]
    [InlineData("zip")] [InlineData("msi")] [InlineData("exe")]
    public async Task PreparedUpdatesUseCorrectInstallationMechanism(string kind)
    {
        using var directory = new TempDirectory();
        var executable = OperatingSystem.IsWindows() ? "tcptunnel.exe" : "tcptunnel";
        var target = Path.Combine(directory.Path, executable); File.WriteAllText(target, "old executable");
        File.WriteAllText(Path.Combine(directory.Path, "install-kind.txt"), kind);
        using var archiveBytes = new MemoryStream();
        if (kind == "zip")
        {
            using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, true))
            { using var stream = archive.CreateEntry(executable).Open(); stream.Write("new executable"u8); }
        }
        else archiveBytes.Write("installer bytes"u8);
        var bytes = archiveBytes.ToArray();
        var name = ReleaseClient.AssetName("2.1.0", ReleaseClient.RuntimeId(), kind);
        var sums = Encoding.UTF8.GetBytes($"{Convert.ToHexString(SHA256.HashData(bytes))}  {name}");
        var release = new AvailableRelease("2.1.0", "", [Asset(name, bytes), Asset("SHA256SUMS", sums)]);
        using var http = Http(url => url.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? sums : bytes);
        System.Diagnostics.ProcessStartInfo? started = null;
        var runtime = new UpdateRuntime(directory.Path, target, true, directory.Path, info => started = info);
        var preparing = UpdateInstaller.PrepareAsync(new ReleaseClient(http, new()), release, new(), TestContext.Current.CancellationToken, runtime);
        if (kind != "zip" && !OperatingSystem.IsWindows()) { await Assert.ThrowsAsync<InvalidOperationException>(() => preparing); return; }
        await preparing; Assert.NotNull(started);
        if (kind == "zip")
        {
            Assert.Equal("--apply-update", started.ArgumentList[0]);
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(started.ArgumentList[1]))!;
            Assert.Equal(target, plan.Target); Assert.Equal("new executable", File.ReadAllText(plan.Staged));
            Assert.Equal("old executable", File.ReadAllText(target));
            Assert.True(started.CreateNoWindow);
        }
        else if (kind == "msi") { Assert.Equal("msiexec.exe", started.FileName); Assert.Equal("/i", started.ArgumentList[0]); }
        else Assert.EndsWith("-setup.exe", started.FileName);
    }

    [Fact]
    public async Task UpdaterRefusesUnknownInstallationKindsAndDevelopmentExecutables()
    {
        using var directory = new TempDirectory(); using var http = Http(_ => []);
        var runtime = new UpdateRuntime(directory.Path, Path.Combine(directory.Path, "app"), false, directory.Path, _ => throw new Exception("Must not launch."));
        var client = new ReleaseClient(http, new()); var release = new AvailableRelease("2.1.0", "", []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.PrepareAsync(client, release, new(), TestContext.Current.CancellationToken, runtime));
        File.WriteAllText(Path.Combine(directory.Path, "install-kind.txt"), "unknown");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.PrepareAsync(client, release, new(), TestContext.Current.CancellationToken, runtime));
        Assert.False(UpdateInstaller.IsSingleFile);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BackgroundUpdateChecksCancelCleanlyOnSuccessAndFailure(bool failure)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var version = "2.2.0";
        var release = new GitHubRelease("v" + version, "", false, false, [Asset(ReleaseClient.AssetName(version, ReleaseClient.RuntimeId(), "zip"), [1]), Asset("SHA256SUMS", [1])]);
        using var http = new HttpClient(new StubHandler(_ => new(failure ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[] { release })) }));
        var client = new ReleaseClient(http, new());
        await Program.MonitorUpdatesAsync(client, new(), stop.Token);
        await Program.MonitorUpdatesAsync(client, new() { CheckOnStartup = false }, TestContext.Current.CancellationToken);
    }
    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)] [InlineData("v2.0.0", "1.99.0", 1)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)] [InlineData("1.0.0-rc.9", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)] [InlineData("1.0.0-a", "1.0.0-a.1", -1)]
    [InlineData("1.0.0+build", "1.0.0", 0)] [InlineData("1.0.0-b", "1.0.0-a", 1)]
    [InlineData("1.0.0", "1.0.0-beta", 1)] [InlineData("1.0.0-alpha", "1.0.0-99", 1)]
    public void SemVerOrdering(string left, string right, int expected) => Assert.Equal(expected, Math.Sign(ReleaseVersion.Parse(left)!.CompareTo(ReleaseVersion.Parse(right))));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("1.0")] [InlineData("01.0.0")] [InlineData("1.0.0-01")]
    [InlineData("1.0.0/../../")] [InlineData("999999999999.0.0")] [InlineData("1.0.0-a..b")]
    public void InvalidVersionsAreRejected(string? text) => Assert.Null(ReleaseVersion.Parse(text));

    private static ReleaseAsset Asset(string name, byte[] data) => new(name, $"https://github.com/tedd/Tedd.TcpTunnel/releases/download/v2.1.0/{name}", data.Length);
    private static AvailableRelease Available(byte[] data, out byte[] sums)
    {
        var name = ReleaseClient.AssetName("2.2.0", ReleaseClient.RuntimeId(), "zip");
        sums = Encoding.UTF8.GetBytes($"{Convert.ToHexString(SHA256.HashData(data))}  {name}\n");
        return new("2.2.0", "https://github.com/tedd/Tedd.TcpTunnel/releases/tag/v2.2.0", [Asset(name, data), Asset("SHA256SUMS", sums)]);
    }

    [Theory]
    [InlineData(false, "2.1.0")] [InlineData(true, "3.0.0-rc.1")]
    public async Task ReleaseSelectionHonorsChannelAndIgnoresDrafts(bool prerelease, string expected)
    {
        var items = new[] { "1.0.0", "2.1.0", "3.0.0-rc.1", "4.0.0", "invalid" }.Select(version => new GitHubRelease("v" + version, "untrusted", version == "4.0.0", version.Contains('-'),
            [Asset(ReleaseClient.AssetName(version, ReleaseClient.RuntimeId(), "zip"), [1]), Asset("SHA256SUMS", [1])])).ToArray();
        using var http = Http(_ => JsonSerializer.SerializeToUtf8Bytes(items));
        var result = await new ReleaseClient(http, new() { IncludePrerelease = prerelease }).CheckAsync("2.0.0", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result!.Version); Assert.StartsWith("https://github.com/tedd/Tedd.TcpTunnel/releases/tag/", result.Page);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)] [InlineData(HttpStatusCode.Forbidden, false)] [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task ReleaseHttpFailuresAreExplicit(HttpStatusCode status, bool noRelease)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        var client = new ReleaseClient(http, new());
        if (noRelease) Assert.Null(await client.CheckAsync("2.0.0", TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckAsync("2.0.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingAssetsAndOldVersionsDoNotOfferUpdates()
    {
        using var http = Http(_ => JsonSerializer.SerializeToUtf8Bytes(new[] { new GitHubRelease("v9.0.0", "", false, false, []) }));
        var client = new ReleaseClient(http, new());
        Assert.Null(await client.CheckAsync("2.0.0", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CheckAsync("bad", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadsVerifyHashAndSize()
    {
        using var directory = new TempDirectory(); var bytes = "verified package bytes"u8.ToArray();
        var release = Available(bytes, out var sums);
        using var http = Http(url => url.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? sums : bytes);
        var file = await new ReleaseClient(http, new()).DownloadAsync(release, "zip", directory.Path, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("hash")] [InlineData("size")] [InlineData("http")] [InlineData("host")] [InlineData("repo")] [InlineData("large")]
    public async Task UnsafeOrCorruptDownloadsAreRejected(string failure)
    {
        using var directory = new TempDirectory(); var bytes = "original bytes"u8.ToArray();
        var release = Available(bytes, out var sums); var asset = release.Assets[0];
        release.Assets[0] = failure switch
        {
            "size" => asset with { Size = bytes.Length + 1 },
            "http" => asset with { Url = asset.Url.Replace("https:", "http:") },
            "host" => asset with { Url = asset.Url.Replace("github.com", "attacker.example") },
            "repo" => asset with { Url = asset.Url.Replace("tedd/", "other/") },
            "large" => asset with { Size = long.MaxValue },
            _ => asset
        };
        if (failure == "hash") bytes[0] ^= 255;
        using var http = Http(url => url.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? sums : bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http, new()).DownloadAsync(release, "zip", directory.Path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CopyLimitRejectsUnboundedStream()
    {
        using var source = new MemoryStream(new byte[100]); using var target = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.CopyBoundedAsync(source, target, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ManifestRequiresExactlyOneValidHash()
    {
        var hash = new string('a', 64);
        Assert.Equal(Convert.FromHexString(hash), ReleaseClient.ReadChecksum($"{hash} *file.zip\r\n", "file.zip"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.ReadChecksum("bad file.zip", "file.zip"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.ReadChecksum($"{hash} file.zip\n{hash} file.zip", "file.zip"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.ReadChecksum($"{hash} other.zip", "file.zip"));
        Assert.Throws<ArgumentException>(() => ReleaseClient.AssetName("1.0.0", "win-x64", "invalid"));
        Assert.EndsWith(".msi", ReleaseClient.AssetName("1.0.0", "win-x64", "msi"));
        Assert.EndsWith("-setup.exe", ReleaseClient.AssetName("1.0.0", "win-x64", "exe"));
    }

    [Theory]
    [InlineData("../escape")] [InlineData("/absolute")] [InlineData("folder/file")] [InlineData("folder\\file")]
    [InlineData("C:escape")] [InlineData("symlink")] [InlineData("duplicate")]
    public void UnsafeZipEntriesCannotBeExtracted(string attack)
    {
        using var directory = new TempDirectory(); var file = Path.Combine(directory.Path, "package.zip");
        using (var archive = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(attack);
            if (attack == "symlink") entry.ExternalAttributes = 0xa000 << 16;
            if (attack == "duplicate") archive.CreateEntry(attack);
        }
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ExtractExecutable(file, Path.Combine(directory.Path, "app")));
    }

    [Fact]
    public void PortableUpdateExtractsOnlyBinaryAndPreservesPreviousVersion()
    {
        using var directory = new TempDirectory();
        var filename = OperatingSystem.IsWindows() ? "tcptunnel.exe" : "tcptunnel";
        var target = Path.Combine(directory.Path, filename); File.WriteAllText(target, "old executable");
        var stage = Path.Combine(directory.Path, ".updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var zip = Path.Combine(stage, "package.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry(filename).Open()) stream.Write("new executable"u8);
            using (var stream = archive.CreateEntry("tunnel.example.json").Open()) stream.Write("do not replace config"u8);
        }
        var staged = Path.Combine(stage, filename); UpdateInstaller.ExtractExecutable(zip, staged);
        var plan = new UpdatePlan(9999999, 0, target, staged, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged))));
        UpdateInstaller.ValidatePlan(plan, stage); UpdateInstaller.ReplaceExecutable(plan);
        Assert.Equal("new executable", File.ReadAllText(target)); Assert.Equal("old executable", File.ReadAllText(target + ".previous"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "tunnel.example.json")));
    }

    [Fact]
    public async Task UpdateHelperWritesSuccessOrFailureResult()
    {
        using var directory = new TempDirectory(); var target = Path.Combine(directory.Path, "tcptunnel"); File.WriteAllText(target, "old");
        var stage = Path.Combine(directory.Path, ".updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var staged = Path.Combine(stage, "tcptunnel"); File.WriteAllText(staged, "new");
        var plan = new UpdatePlan(int.MaxValue, 0, target, staged, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged))));
        var file = Path.Combine(stage, "plan.json"); File.WriteAllText(file, JsonSerializer.Serialize(plan));
        Assert.Equal(0, await UpdateInstaller.ApplyAsync(file, TestContext.Current.CancellationToken));
        Assert.Contains("installed", File.ReadAllText(Path.Combine(stage, "result.txt")));
        File.WriteAllText(file, "null");
        Assert.Equal(1, await UpdateInstaller.ApplyAsync(file, TestContext.Current.CancellationToken));
        Assert.Contains("failed", File.ReadAllText(Path.Combine(stage, "result.txt")));
    }

    [Fact]
    public void PlanRejectsTraversalAndTampering()
    {
        using var directory = new TempDirectory(); var target = Path.Combine(directory.Path, "tcptunnel"); File.WriteAllText(target, "old");
        var stage = Path.Combine(directory.Path, ".updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var staged = Path.Combine(stage, "tcptunnel"); File.WriteAllText(staged, "new");
        var plan = new UpdatePlan(0, 0, target, staged, new string('a', 64));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ValidatePlan(plan with { Staged = target }, stage));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ValidatePlan(plan, directory.Path));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ReplaceExecutable(plan));
        Assert.Equal("old", File.ReadAllText(target));
    }

    private static HttpClient Http(Func<string, byte[]> response) => new(new StubHandler(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request.RequestUri!.AbsoluteUri)) }));
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
