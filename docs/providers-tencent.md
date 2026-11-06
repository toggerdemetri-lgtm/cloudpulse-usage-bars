# 腾讯云 CVM

选择腾讯云后，填写 CVM 实例 ID（`ins-…`）、实例地域（如 `ap-guangzhou`）以及腾讯云 `SecretId` / `SecretKey`。临时凭证同时填写 `Token`。凭证厂商必须与设置一致，适配器在请求前拒绝其他厂商凭证。

调用腾讯云官方 .NET SDK 的 `GetMonitorData`，固定 HTTPS 域名 `monitor.tencentcloudapi.com`、命名空间 `QCE/CVM`、维度 `InstanceId`（区分大小写），每次查询最近十分钟、周期 60 秒。每项独立读取并处理失败；缺失数据不会转成零。客户端请求超时为十秒，每项等待上限为十二秒；取消时显示安全的本项超时提示。

| 色条/提示数据 | 当前 CVM 指标 | 60 秒统计 | 原单位 | 程序数值 |
| --- | --- | --- | --- | --- |
| CPU | `CpuUsage` | 平均值 | % | 百分比 |
| 内存 | `MemUsage` | 平均值 | % | 百分比 |
| 磁盘 | `CvmDiskUsage` | 最大值 | % | 百分比 |
| 公网入站 | `WanIntraffic` | 最大值 | Mbps | 乘 1,000,000 转 bit/s |
| 公网出站 | `WanOuttraffic` | 最大值 | Mbps | 乘 1,000,000 转 bit/s |

以上名称按[当前 CVM 指标文档](https://cloud.tencent.com/document/product/248/6843)采用；通用 API 示例中的旧 `CPUUsage` 拼写不作为本适配器的指标来源。网络使用每分钟最大公网带宽，出站色条除以用户填写的出站带宽计算占比。公网统计不含 IPv6；标准账户口径包含绑定到 CVM 的所有 EIP 和公网 CLB 的公网带宽总和，具体范围以官方指标说明为准。

`CvmDiskUsage` 已是所有磁盘使用率的最大值，不含磁盘名称维度。本版本只支持该汇总值；填写“磁盘设备”时，磁盘项会提示清空设置，其他项继续更新。需要特定磁盘时，应后续接入 `DiskUsage` 的 `InstanceId + diskname` 维度，不能把汇总数据当作该盘数据。

CPU、内存和磁盘数据的可用性依赖实例的监控采集组件与上报状态。没有数据时，请核对地域、实例、监控组件及公网 IP；适配器保留服务端实际采样时间，由统一规则判断过期，不以本地查询时刻替代采样时刻。

权限可使用腾讯云监控只读策略 `QcloudMonitorReadOnlyAccess`；本适配器只调用 `monitor:GetMonitorData`，不枚举和修改 CVM。需要更小权限范围时，可按官方 CAM 支持范围配置该动作。参见[监控权限授予说明](https://cloud.tencent.com/document/product/248/45428)、[CAM 支持的监控动作](https://cloud.tencent.com/document/product/598/67487)及[官方只读策略与 GetMonitorData 对照](https://cloud.tencent.com/document/product/1416/127314)。

接口调用可能计入监控 API 请求额度，具体规则见[GetMonitorData 文档](https://cloud.tencent.com/document/api/248/31014)。本版本尚未使用真实腾讯云凭证在线联调；验证使用官方 SDK 请求对象、伪响应及原始响应样本，不代表已验证任一用户实例。

实现说明：SDK `3.0.1520` 的 `DataPoint.Timestamps` 使用 `float?[]`，会损失 Unix 秒精度。适配器通过官方 SDK 客户端的受保护 `InternalRequest` 完成签名、HTTPS 和 API 错误处理，随后自行解析原始 JSON 中的时间戳和数值，避免该转换。该方法见[官方 SDK AbstractClient 实现](https://github.com/TencentCloud/tencentcloud-sdk-dotnet/blob/master/TencentCloud/Common/AbstractClient.cs)。错误提示仅采用本地固定文案，不显示 SDK 异常正文、签名请求或凭证。
