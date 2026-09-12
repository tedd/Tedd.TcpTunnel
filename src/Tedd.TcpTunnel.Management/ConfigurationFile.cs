using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedd.TcpTunnel.Management;

public sealed record ConfigurationDocument(string Json, string Revision);

public static class ConfigurationFile
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static TunnelOptions Parse(string json)
    {
        var options = JsonSerializer.Deserialize<TunnelOptions>(json, Json) ?? throw new ArgumentException("Configuration cannot be null.");
        options.Validate();
        return options;
    }

    public static ConfigurationDocument Read(string path)
    {
        var json = File.ReadAllText(path);
        return new(json, Revision(json));
    }

    public static string Revision(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static ConfigurationDocument Save(string path, string json, string? expectedRevision)
    {
        _ = Parse(json);
        path = Path.GetFullPath(path);
        var exists = File.Exists(path);
        if (exists && (expectedRevision is null || Read(path).Revision != expectedRevision))
            throw new InvalidOperationException("The configuration changed on disk. Reload it before saving.");
        if (!exists && expectedRevision is not null) throw new InvalidOperationException("The configuration was removed. Reload it before saving.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = CreatePrivateFile(temporary))
            {
                file.Write(Encoding.UTF8.GetBytes(json));
                file.Flush(true);
            }
            if (exists && Read(path).Revision != expectedRevision)
                throw new InvalidOperationException("The configuration changed on disk. Reload it before saving.");
            // Replace preserves the existing Windows ACL, including custom service-account access.
            if (exists) File.Replace(temporary, path, null);
            else File.Move(temporary, path, false);
            return new(json, Revision(json));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }
}
