# 华为云 ECS / CES

使用华为云官方 NuGet 包 `HuaweiCloud.SDK.Ces` 3.1.216。配置选择华为云，填写 ECS 实例 UUID、实例地域以及该地域对应的 32 位项目 ID。AK/SK 必须属于华为云；临时凭证可携带 SecurityToken。客户端仅使用应用传入的凭证和项目 ID，显式连接 `https://ces.{region}.myhuaweicloud.com`，不使用本机 SDK 凭证链，也不自动查询 IAM 项目。

所需只读权限是 `ces:metrics:list` 和 `ces:metricData:get`（后者文档列出别名 `ces:metricData:list`）。无需授予 ECS 修改、告警创建或 Agent 安装权限。应用不远程安装 Agent。

| 显示项 | 命名空间 / 指标 | 维度 | 采样和说明 |
| --- | --- | --- | --- |
| CPU | `SYS.ECS / cpu_util` | `instance_id` | 基础监控，按 300 秒聚合 |
| 内存 | `AGT.ECS / mem_usedPercent` | `instance_id` | 需要 CES Agent，按 60 秒聚合 |
| 磁盘 | `AGT.ECS / disk_usedPercent` | 从 ListMetrics 原样发现 `instance_id`、`mount_point` 等维度 | 需要 CES Agent，按 60 秒聚合 |
| 网络入 / 出 | `SYS.ECS / network_incoming_bytes_aggregate_rate`、`network_outgoing_bytes_aggregate_rate` | `instance_id` | 虚拟化层带外网卡总流量，按 300 秒聚合，原值 byte/s 乘 8 转为 bit/s |

网络指标包含内网流量，不能用于声称“公网专属使用率”；带宽比例须按实际监控口径理解。SR-IOV、实例虚拟化类型和 VMTools 可能影响带外网络指标是否上报，应用没有用 Agent 网卡统计或其他指标悄悄替代。基础监控原始采样通常每 5 分钟一次，即使应用每 60 秒刷新，也不会产生新的每分钟基础样本。每条样本保留 CES 返回的真实毫秒时间，基础监控携带 300 秒采样周期，Agent 数据携带 60 秒周期，供应用判断过期；不会用查询时间替代采样时间。

每次读取最近 10 分钟。磁盘通过 ListMetrics 的 `meta_data.marker` 逐页发现，按 Total 判断完成；重复游标或缺失分页信息导致无法确认完整时，该项失败。`mount_point` 的值可能包含实例 UUID 前缀和经过 CES 规则转换的挂载路径，请将控制台/API 中返回的完整维度值填入磁盘设备配置，应用原样匹配，不自行把 `/` 改写为设备名。未指定设备时，先取每个挂载点最新样本，再取最高使用率；指定设备时只取该维度的最新样本。

未安装 Agent、没有监控样本、API 权限不足、网络错误等情况保留缺失状态并显示灰色，不制造零值。单项失败不覆盖其他正常指标。若某个已发现且符合设备筛选的磁盘查询失败、返回空数据或全部样本无效，磁盘整体显示“磁盘指标不完整”，避免用不完整集合给出偏低的“最高使用率”。每项指标具有 10 秒总时限，包括多页发现与多盘查询；应用统一约束 SDK 和注入的传输，即使传输忽略取消，也会按时返回安全的超时提示。已取消的轮询不会继续发出请求。凭证或 SDK 异常只显示固定安全提示，不把签名请求、SDK 异常正文或 Secret 展示到界面。

本次通过离线 SDK 响应测试验证请求维度、时间、单位、磁盘分页与各项隔离；未使用真实 AK/SK 做在线联调。

官方依据：

- [官方 NuGet 包](https://www.nuget.org/packages/HuaweiCloud.SDK.Ces/3.1.216)
- [ShowMetricData：权限、粒度、毫秒时间](https://support.huaweicloud.com/api-ces/ces_03_0033.html)
- [ListMetrics：权限、维度、marker 分页](https://support.huaweicloud.com/api-ces/ces_03_0023.html)
- [ECS 基础监控指标、实例限制与采样周期](https://support.huaweicloud.com/usermanual-ecs/ecs_03_1002.html)
- [CES Agent 指标列表](https://support.huaweicloud.com/ces_faq/ces_faq_2513.html)
- [官方 .NET SDK](https://github.com/huaweicloud/huaweicloud-sdk-net-v3)
