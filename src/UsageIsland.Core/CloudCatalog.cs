namespace UsageIsland.Core;

public static class CloudCatalog
{
    public static string Name(CloudVendor vendor) => vendor switch { CloudVendor.Aliyun => "阿里云 ECS", CloudVendor.Tencent => "腾讯云 CVM", CloudVendor.Huawei => "华为云 ECS", CloudVendor.Aws => "AWS EC2", _ => "未知云厂商" };
    public static IslandSettings Defaults(CloudVendor vendor) => new()
    {
        Vendor = vendor, InstanceId = vendor == CloudVendor.Aliyun ? new IslandSettings().InstanceId : "",
        RegionId = vendor switch { CloudVendor.Tencent => "ap-beijing", CloudVendor.Huawei => "cn-north-4", CloudVendor.Aws => "us-east-1", _ => "cn-beijing" }
    };
    public static string NetworkName(CloudVendor vendor) => vendor is CloudVendor.Aws or CloudVendor.Huawei ? "网卡" : "公网";
    public static string CredentialIdLabel(CloudVendor vendor) => vendor switch { CloudVendor.Tencent => "SecretId", CloudVendor.Huawei => "Access Key（AK）", CloudVendor.Aws => "Access Key ID", _ => "RAM AccessKey ID" };
    public static string CredentialSecretLabel(CloudVendor vendor) => vendor switch { CloudVendor.Tencent => "SecretKey", CloudVendor.Huawei => "Secret Access Key（SK）", CloudVendor.Aws => "Secret Access Key", _ => "AccessKey Secret" };
}
