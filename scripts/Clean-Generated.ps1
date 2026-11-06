param([switch]$Preview)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$projectPath = Join-Path $workspaceRoot 'src\UsageIsland.App\UsageIsland.App.csproj'
$version = [string]([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号无效，已停止清理。' }
$keepNames = @("云脉·用量颜色条-$version-win-x64.zip", "云脉·用量颜色条-$version-source.zip", "云脉-$version-SHA256SUMS.txt")
foreach ($name in $keepNames[0..1]) {
    if (-not (Test-Path -LiteralPath (Join-Path $workspaceRoot "artifacts\$name") -PathType Leaf)) { throw '最新交付包不完整，已停止清理。' }
}
$targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $workspaceRoot 'artifacts') -Force) {
    if ($item.Name -notin $keepNames) { [void]$targets.Add($item.FullName) }
}
foreach ($relative in @('.tools\tencent-test', '.tools\huawei-test', '.tools\aws-test', '.tools\Inspect-CloudPulse.ps1', '.tools\Observe-CloudPulseDrag.ps1', '.tools\dotnet-install.ps1', '.tools\cli\.dotnet\TelemetryStorageService')) {
    $candidate = Join-Path $workspaceRoot $relative
    if (Test-Path -LiteralPath $candidate) { [void]$targets.Add($candidate) }
}
foreach ($folder in @('src', 'tests')) {
    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $workspaceRoot $folder) -Directory -Recurse -Force | Where-Object { $_.Name -in @('bin', 'obj') }) {
        [void]$targets.Add($item.FullName)
    }
}
# Verify all absolute targets before deleting any item. Refuse junctions/symlinks.
$bytes = 0L
foreach ($target in $targets) {
    $full = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $target).Path)
    if (-not $full.StartsWith($workspaceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "清理目标越出项目：$full" }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "拒绝清理重解析点：$full" }
    if ($item.PSIsContainer) {
        $children = @(Get-ChildItem -LiteralPath $full -Recurse -Force)
        if (@($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw "目标包含链接，已停止：$full" }
        $bytes += ($children | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum
    } else { $bytes += $item.Length }
}
if (-not $Preview) {
    foreach ($target in $targets | Sort-Object { $_.Length } -Descending) {
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
[pscustomobject]@{ Preview=[bool]$Preview; Version=$version; RemovedItems=if($Preview){0}else{$targets.Count}; Bytes=$bytes; Retained='最新便携ZIP、源码ZIP、校验码、源码与测试源码、SDK和依赖'; UserConfiguration='未访问或删除' } | ConvertTo-Json -Compress
