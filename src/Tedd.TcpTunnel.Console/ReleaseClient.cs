using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tedd.TcpTunnel.Console;

internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Url,
    [property: JsonPropertyName("size")] long Size);
internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("html_url")] string Page,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] ReleaseAsset[] Assets);
internal sealed record AvailableRelease(string Version, string Page, ReleaseAsset[] Assets);

internal sealed partial record ReleaseVersion(Version Core, string Prerelease) : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    public static ReleaseVersion? Parse(string? text)
    {
        if (text is null || text.Length > 128) return null;
        var match = Pattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return null;
        var pre = match.Groups[4].Value;
        if (pre.Split('.').Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return null;
        return new(new Version(major, minor, patch), pre);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var core = Core.CompareTo(other.Core); if (core != 0) return core;
        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        var a = Prerelease.Split('.'); var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = a[i].All(char.IsAsciiDigit); var bn = b[i].All(char.IsAsciiDigit);
            var result = an && bn ? a[i].Length.CompareTo(b[i].Length) : an != bn ? an ? -1 : 1 : 0;
            if (result == 0) result = string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }
}

internal sealed class ReleaseClient
{
    private readonly HttpClient _http;
    private readonly UpdateOptions _options;
    internal const long MaxDownloadBytes = 512L * 1024 * 1024;
    public ReleaseClient(HttpClient http, UpdateOptions options)
    {
        options.Validate(); _http = http; _options = options;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tedd.TcpTunnel", "2.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AvailableRelease?> CheckAsync(string installed, CancellationToken token)
    {
        var current = ReleaseVersion.Parse(installed) ?? throw new ArgumentException("Installed version is not valid SemVer.");
        using var response = await _http.GetAsync($"https://api.github.com/repos/{_options.Repository}/releases?per_page=100", token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        await CopyBoundedAsync(stream, bounded, 4 * 1024 * 1024, token).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<GitHubRelease[]>(bounded.GetBuffer().AsSpan(0, (int)bounded.Length)) ?? [];
        return releases.Where(r => r is not null && !r.Draft && r.Assets is not null && r.Assets.All(a => a is not null && !string.IsNullOrEmpty(a.Name)) && (_options.IncludePrerelease || !r.Prerelease))
            .Select(r => (Release: r, Version: ReleaseVersion.Parse(r.Tag)))
            .Where(r => r.Version is not null && r.Version.CompareTo(current) > 0 && (_options.IncludePrerelease || r.Version.Prerelease.Length == 0))
            .OrderByDescending(r => r.Version)
            .Select(r => new AvailableRelease(r.Release.Tag.TrimStart('v'), $"https://github.com/{_options.Repository}/releases/tag/{Uri.EscapeDataString(r.Release.Tag)}", r.Release.Assets))
            .FirstOrDefault(r => r.Assets.Any(a => a.Name == AssetName(r.Version, RuntimeId(), "zip")) && r.Assets.Any(a => a.Name == "SHA256SUMS"));
    }

    internal static string RuntimeId()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : throw new PlatformNotSupportedException("Updates support Windows and Linux.");
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new PlatformNotSupportedException("Updates support x64 and arm64.") };
        return $"{platform}-{architecture}";
    }
    internal static string AssetName(string version, string rid, string kind) => kind switch
    {
        "zip" => $"tcptunnel-{version}-{rid}.zip",
        "msi" => $"tcptunnel-{version}-{rid}.msi",
        "exe" => $"tcptunnel-{version}-{rid}-setup.exe",
        _ => throw new ArgumentException("Unknown package kind.")
    };

    public async Task<string> DownloadAsync(AvailableRelease release, string kind, string directory, CancellationToken token)
    {
        var name = AssetName(release.Version, RuntimeId(), kind);
        var asset = release.Assets.SingleOrDefault(a => a.Name == name) ?? throw new IOException($"Release does not contain {name}.");
        var manifest = release.Assets.SingleOrDefault(a => a.Name == "SHA256SUMS") ?? throw new IOException("Release has no SHA256SUMS manifest.");
        using var checksums = new MemoryStream();
        await DownloadAssetAsync(manifest, checksums, 65536, token).ConfigureAwait(false);
        var expected = ReadChecksum(System.Text.Encoding.UTF8.GetString(checksums.GetBuffer(), 0, (int)checksums.Length), name);
        var path = Path.Combine(directory, name);
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await DownloadAssetAsync(asset, file, MaxDownloadBytes, token).ConfigureAwait(false);
            await using var verify = File.OpenRead(path);
            var actual = await SHA256.HashDataAsync(verify, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidDataException("Downloaded package failed SHA-256 verification.");
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    internal static byte[] ReadChecksum(string manifest, string filename)
    {
        var matches = manifest.Split('\n').Select(line => line.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == filename).ToArray();
        if (matches.Length != 1 || matches[0][0].Length != 64 || !matches[0][0].All(char.IsAsciiHexDigit)) throw new InvalidDataException("Missing or ambiguous SHA-256 checksum.");
        return Convert.FromHexString(matches[0][0]);
    }

    private async Task DownloadAssetAsync(ReleaseAsset asset, Stream target, long maximum, CancellationToken token)
    {
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || !url.AbsolutePath.StartsWith($"/{_options.Repository}/releases/download/", StringComparison.Ordinal) ||
            asset.Size <= 0 || asset.Size > maximum) throw new InvalidDataException("Invalid release asset URL or size.");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var length = await CopyBoundedAsync(source, target, maximum, token).ConfigureAwait(false);
        if (length != asset.Size) throw new InvalidDataException("Release asset size mismatch.");
    }

    internal static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken token)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        long total = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += count; if (total > maximum) throw new InvalidDataException("Download exceeds size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            return total;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}
