param([switch]$SkipTests, [switch]$NativeSmoke)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) {
    $dotnetExe = $localSdk
    $env:DOTNET_ROOT = Split-Path -Parent $localSdk
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
} else {
    $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_NOLOGO = '1'
function Invoke-DotNet([string[]]$Arguments) {
    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 失败，退出码 $LASTEXITCODE" }
}
if (-not $SkipTests) { Invoke-DotNet -Arguments @('run', '--project', 'tests/UsageIsland.Tests', '-c', 'Release') }
if (-not $SkipTests) { Invoke-DotNet -Arguments @('run', '--project', 'tests/UsageIsland.ProviderTests', '-c', 'Release') }
if ($NativeSmoke) { Invoke-DotNet -Arguments @('run', '--project', 'tests/UsageIsland.NativeSmoke', '-c', 'Release') }
$projectXml = [xml](Get-Content -LiteralPath 'src\UsageIsland.App\UsageIsland.App.csproj' -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
$portable = Join-Path $PSScriptRoot "artifacts\portable\$version\CloudPulse"
Invoke-DotNet -Arguments @('publish', 'src/UsageIsland.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $portable)
Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $portable '使用说明.md') -Force
Copy-Item -LiteralPath 'ram-readonly-policy.json' -Destination $portable -Force
if (Test-Path -LiteralPath 'docs\验收记录.md') { Copy-Item -LiteralPath 'docs\验收记录.md' -Destination $portable -Force }
Copy-Item -LiteralPath 'docs\第三方组件.md' -Destination $portable -Force
Get-ChildItem -LiteralPath 'docs' -Filter 'providers-*.md' -File | Copy-Item -Destination $portable -Force
$portableZip = Join-Path $PSScriptRoot "artifacts\云脉·用量颜色条-$version-win-x64.zip"
Compress-Archive -LiteralPath $portable -DestinationPath $portableZip -Force
$sourceStage = Join-Path $PSScriptRoot 'artifacts\source\UsageIsland-source'
New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
$sourceFiles = Get-ChildItem -LiteralPath 'src','tests','docs','scripts' -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $sourceFiles) {
    $relative = [System.IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    $destination = Join-Path $sourceStage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
foreach ($file in @('README.md','build.ps1','Directory.Build.props','.gitignore','ram-readonly-policy.json')) { Copy-Item -LiteralPath $file -Destination $sourceStage -Force }
$sourceZip = Join-Path $PSScriptRoot "artifacts\云脉·用量颜色条-$version-source.zip"
Compress-Archive -LiteralPath $sourceStage -DestinationPath $sourceZip -Force
Get-FileHash -LiteralPath $portableZip, $sourceZip -Algorithm SHA256 | Format-Table -AutoSize
