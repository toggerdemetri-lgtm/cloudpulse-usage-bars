using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageIsland.Core;

namespace UsageIsland.App;

public sealed record CloudCredentials(string AccessKeyId, string AccessKeySecret, CloudVendor Vendor = CloudVendor.Aliyun, string? SessionToken = null)
{
    public override string ToString() => "[已隐藏的云凭证]";
}
public sealed record LoadedConfiguration(IslandSettings Settings, CloudCredentials? Credentials, string? Warning);
public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsageIsland/credentials/v1");
    private sealed record StoredProfile(IslandSettings Settings, string? ProtectedCredentials);
    private sealed record StoredConfiguration(IslandSettings Settings, string? ProtectedCredentials, Dictionary<CloudVendor, StoredProfile>? Profiles = null);
    public string FilePath { get; }
    public ConfigurationStore(string directory) => FilePath = System.IO.Path.Combine(directory, "settings.json");
    public LoadedConfiguration Load()
    {
        if (!File.Exists(FilePath)) return new(new(), null, null);
        try
        {
            var stored = JsonSerializer.Deserialize<StoredConfiguration>(File.ReadAllText(FilePath), JsonOptions);
            if (stored?.Settings is null || !Enum.IsDefined(stored.Settings.Vendor)) return new(new(), null, "配置文件无效，请打开设置重新保存。原文件未修改。");
            return Decode(new(stored.Settings, stored.ProtectedCredentials), stored.Settings.Vendor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or ArgumentException)
        {
            return new(new(), null, "配置无法读取或凭证无法解密，请在本机重新填写。原文件未修改。");
        }
    }
    public LoadedConfiguration LoadProfile(CloudVendor vendor)
    {
        if (!Enum.IsDefined(vendor)) throw new ArgumentException("云厂商无效。");
        var defaults = CloudCatalog.Defaults(vendor);
        if (!File.Exists(FilePath)) return new(defaults, null, null);
        try
        {
            var stored = JsonSerializer.Deserialize<StoredConfiguration>(File.ReadAllText(FilePath), JsonOptions);
            if (stored?.Settings?.Vendor == vendor) return Decode(new(stored.Settings, stored.ProtectedCredentials), vendor);
            if (stored?.Profiles is not null && stored.Profiles.TryGetValue(vendor, out var profile)) return Decode(profile, vendor);
            return new(defaults, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or ArgumentException)
        {
            return new(defaults, null, "该厂商配置无法读取，请重新填写；原文件未修改。");
        }
    }
    private static LoadedConfiguration Decode(StoredProfile? profile, CloudVendor expected)
    {
        if (profile?.Settings is null || profile.Settings.Vendor != expected || profile.Settings.Validate().Count != 0) return new(CloudCatalog.Defaults(expected), null, "配置文件无效，请重新填写；原文件未修改。");
        CloudCredentials? credentials = null;
        if (profile.ProtectedCredentials is not null)
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(profile.ProtectedCredentials), Entropy, DataProtectionScope.CurrentUser);
            try { credentials = JsonSerializer.Deserialize<CloudCredentials>(clear, JsonOptions); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            if (credentials is null || credentials.Vendor != expected || string.IsNullOrWhiteSpace(credentials.AccessKeyId) || string.IsNullOrWhiteSpace(credentials.AccessKeySecret)) return new(profile.Settings, null, "凭证无效或厂商不匹配，请在本机重新填写。");
        }
        return new(profile.Settings, credentials, null);
    }
    public void Save(IslandSettings settings, CloudCredentials? credentials)
    {
        if (settings.Validate().Count != 0) throw new ArgumentException("配置内容无效。");
        string? encrypted = null;
        if (credentials is not null)
        {
            if (credentials.Vendor != settings.Vendor) throw new ArgumentException("凭证所属云厂商与所选厂商不一致，请重新填写。");
            if (string.IsNullOrWhiteSpace(credentials.AccessKeyId) || string.IsNullOrWhiteSpace(credentials.AccessKeySecret)) throw new ArgumentException("请填写完整的 AccessKey ID 和 Secret。");
            var clear = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var profiles = new Dictionary<CloudVendor, StoredProfile>();
        if (File.Exists(FilePath))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<StoredConfiguration>(File.ReadAllText(FilePath), JsonOptions);
                if (previous?.Profiles is not null)
                    foreach (var pair in previous.Profiles)
                        if (Enum.IsDefined(pair.Key) && pair.Value?.Settings is not null && pair.Value.Settings.Vendor == pair.Key) profiles[pair.Key] = pair.Value;
                // Also migrate pre-multicloud files without decrypting another vendor's key.
                if (previous?.Settings is not null && Enum.IsDefined(previous.Settings.Vendor) && previous.Settings.Validate().Count == 0)
                    profiles[previous.Settings.Vendor] = new(previous.Settings, previous.ProtectedCredentials);
            }
            catch (JsonException) { /* Explicit Save can replace a malformed legacy file. */ }
        }
        profiles[settings.Vendor] = new(settings, encrypted);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoredConfiguration(settings, encrypted, profiles), JsonOptions));
        File.Move(temp, FilePath, true);
    }
}
