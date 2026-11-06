# AWS EC2 / CloudWatch

选择 AWS，填写 EC2 所在地域与实例 ID。支持全球 AWS 和中国区（例如 `cn-north-1`、`cn-northwest-1`）；SDK 按地域选择官方 HTTPS 端点。中国区账户与全球账户的凭证不可混用。应用仅使用设置中明确保存的 AWS Access Key / Secret 和可选 Session Token，不自动读取环境变量、共享凭证文件或实例角色。跨厂商凭证会在发出请求前被拒绝。[官方端点](https://docs.aws.amazon.com/general/latest/gr/cw_region.html)、[SDK 凭证解析顺序](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html)。

## 指标与采样周期

| 显示项 | CloudWatch 指标 | 统计 / 单位 |
| --- | --- | --- |
| CPU | `AWS/EC2` / `CPUUtilization` | `Average` / Percent |
| 入站、出站 | `AWS/EC2` / `NetworkIn`、`NetworkOut` | `Sum` / Bytes；除采样周期再乘 8 得到 bit/s |
| 内存 | `CWAgent` / `mem_used_percent` | `Average` / Percent |
| 磁盘 | `CWAgent` / `disk_used_percent` | `Average` / Percent |

EC2 默认采用基本监控的 300 秒周期。只有实例已经开启详细监控时，才勾选设置中的“该 EC2 已开启详细监控（1分钟）”，应用随后改用 60 秒查询；这个选项不会开启 AWS 服务，也不会调用修改 API。基本监控显示五分钟平均值，刷新界面不会令它变成即时数据。网络指标统计全部网卡的字节量，包含内网与公网流量，不能视为公网出站流量。带宽占比应使用与此口径相符的容量；单纯公网带宽限额可能无法解释内网流量占比。[EC2 指标定义](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/viewing_metrics_with_cloudwatch.html)、[基本与详细监控](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/manage-detailed-monitoring.html)。

查询最近 15 分钟并保留 AWS 返回的采样时间、周期。EC2 查询截止时间对齐已结束的采样桶，避免拿未完成桶的部分字节量计算整周期速率。最新值按采样时间选择，不按响应顺序；缺失、无效或失败数据不会补零。五分钟采样超过 720 秒后过期，一分钟采样超过 180 秒后过期。五分钟桶在结束后才完整可用，因此按两个周期另加 120 秒发布余量判断过期。时间来自采样桶起点，并非本次刷新时间。

## 官方 CloudWatch Agent

内存和磁盘使用率需要在 EC2 上配置官方 CloudWatch Agent。当前适配读取 Linux/macOS 的上述指标名称；Windows Performance Monitor 默认指标名称不同，当前不会自动转换这些名称。CPU、网络仍可独立读取。Agent 使用 `CWAgent` 命名空间，至少配置以下内容（应合并到现有配置）：

```json
{
  "agent": { "metrics_collection_interval": 60 },
  "metrics": {
    "namespace": "CWAgent",
    "append_dimensions": { "InstanceId": "${aws:InstanceId}" },
    "metrics_collected": {
      "mem": { "measurement": ["used_percent"] },
      "disk": { "measurement": ["used_percent"], "resources": ["*"] }
    }
  }
}
```

Agent 上报需要实例角色的写入权限，和本桌面程序的只读凭证分开配置。安装、角色和启动步骤见[官方 Agent 文档](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/Install-CloudWatch-Agent.html)，配置字段见[配置说明](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-Agent-Configuration-File-Details.html)，各操作系统的指标见[官方指标表](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/metrics-collected-by-CloudWatch-agent.html)。

应用先用 `ListMetrics` 按 `InstanceId` 发现指标，再携带每条指标的全部维度请求 `GetMetricStatistics`；不会把 `ImageId`、`InstanceType`、`path`、`device`、`fstype` 等已上报维度丢掉。新指标可能需要最多 15 分钟才能在发现列表出现。磁盘设置支持精确的 `path`（如 `/`、`/data`）或 `device`；默认分别取每个磁盘最新采样，再选择使用率最高的一盘。已发现且纳入选择的任一磁盘没有有效采样时，云盘项提示“指标不完整”并显示灰色，不会把其余磁盘的最大值当作完整结果。固定磁盘时只检查匹配的磁盘，不受未选磁盘缺失影响；其他指标独立更新。返回磁盘身份优先使用 `path`。[ListMetrics](https://docs.aws.amazon.com/AmazonCloudWatch/latest/APIReference/API_ListMetrics.html)、[GetMetricStatistics 维度规则](https://docs.aws.amazon.com/AmazonCloudWatch/latest/APIReference/API_GetMetricStatistics.html)。

## 桌面程序只读 IAM 策略

当前实现实际调用 `cloudwatch:ListMetrics` 与 `cloudwatch:GetMetricStatistics`：

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["cloudwatch:ListMetrics", "cloudwatch:GetMetricStatistics"],
    "Resource": "*"
  }]
}
```

`GetMetricData` 对应独立权限 `cloudwatch:GetMetricData`，当前实现不调用它，无需额外授予。`GetMetricStatistics` 无实例级资源 ARN 授权；这里使用 `Resource: "*"`，程序按实例筛选不构成 IAM 访问隔离。应用不需要 `ec2:MonitorInstances`、`cloudwatch:PutMetricData` 或管理员权限。[官方 API / IAM 动作映射](https://docs.aws.amazon.com/service-authorization/latest/reference/list_cloudwatch.html)。

## 边界与验证

每个显示项独立查询，SDK 单次请求最长 5 秒且不重试，每项总截止时间 10 秒；超时/权限不足不会阻止其他项更新。Agent 指标发现最多 10 页、每项最多 64 条独立维度组合，维度去重且检测重复分页令牌；超限明确提示，避免静默显示不完整的最大磁盘值。SDK 异常消息、响应体和签名请求不会进入界面，提示为固定中文文案。

行为测试使用构造注入的传输委托和虚构数据，不访问真实 AWS 账户。覆盖单位转换、完整维度、分页、最新采样、指定磁盘、缺失值、部分失败、凭证厂商拦截、异常脱敏与有界取消。真实账户/Agent 的在线联调尚未执行。
