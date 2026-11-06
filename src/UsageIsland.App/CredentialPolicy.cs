using System;
using UsageIsland.Core;

namespace UsageIsland.App;

public static class CredentialPolicy
{
    public static CloudCredentials? Resolve(CloudVendor vendor, string id, string secret, string? token, CloudCredentials? existing, bool clear)
    {
        if (clear) return null;
        id = id.Trim(); secret = secret.Trim();
        if (secret.Length == 0 && existing?.Vendor == vendor && existing.AccessKeyId == id) secret = existing.AccessKeySecret;
        if (id.Length == 0 && secret.Length == 0) return null;
        if (id.Length == 0 || secret.Length == 0) throw new ArgumentException("请填写当前厂商完整的密钥 ID 和 Secret；更换 ID 时必须同时填写 Secret。");
        return new(id, secret, vendor, string.IsNullOrWhiteSpace(token) ? null : token.Trim());
    }
}
