<#
.SYNOPSIS
    一键构建并打包 LimoClip Windows x64 自包含免环境绿色版 Release ZIP。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 -Version v0.5.0-beta.1
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "v0.5.0-beta.1",

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$Runtime = "win-x64",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$DesktopProject = Join-Path $RepoRoot "src\LiveMonitor.Desktop\LiveMonitor.Desktop.csproj"
$ToolsDir = Join-Path $RepoRoot "tools"
$ArtifactsRoot = Join-Path $RepoRoot "artifacts"
$PackageName = "LimoClip-$Version-$Runtime"
$PublishDir = Join-Path $ArtifactsRoot $PackageName
$ZipPath = Join-Path $ArtifactsRoot "$PackageName.zip"
$HashPath = Join-Path $ArtifactsRoot "SHA256SUMS.txt"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  LimoClip Release Packager ($Version)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. 校验外部依赖二进制是否齐全
$RequiredTools = @(
    "cacert.crt",
    "libmpv-2.dll",
    "yt-dlp.exe",
    "ffmpeg\ffmpeg.exe"
)

$MissingTools = @()
foreach ($rel in $RequiredTools) {
    $full = Join-Path $ToolsDir $rel
    if (-not (Test-Path $full)) {
        $MissingTools += $rel
    }
}

if ($MissingTools.Count -gt 0) {
    Write-Error ("tools/ 目录下缺少必需运行文件: {0}`n请先运行: powershell -ExecutionPolicy Bypass -File .\scripts\fetch-tools.ps1" -f ($MissingTools -join ", "))
}

# 2. 运行单元测试（可用 -SkipTests 跳过）
if (-not $SkipTests) {
    Write-Host "`n[1/5] 运行全量单元测试..." -ForegroundColor Yellow
    Push-Location $RepoRoot
    try {
        dotnet test LiveMonitor.sln -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "单元测试未通过，已中止打包。" }
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "`n[1/5] 已跳过单元测试 (-SkipTests)" -ForegroundColor DarkGray
}

# 3. 清理并执行自包含发布（用户无需单独安装 .NET 运行时）
Write-Host "`n[2/5] 执行自包含发布 ($Runtime)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

$AssemblyVersion = $Version.TrimStart('v', 'V').Split('-')[0]
dotnet publish $DesktopProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $PublishDir `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:InformationalVersion=$Version `
    -p:Version=$AssemblyVersion `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败。" }

# 4. 确保 tools/ 运行依赖与协议文件完整打入发布包
Write-Host "`n[3/5] 复制 tools/ 依赖与开源协议..." -ForegroundColor Yellow
$OutToolsDir = Join-Path $PublishDir "tools"
New-Item -ItemType Directory -Path $OutToolsDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutToolsDir "ffmpeg") -Force | Out-Null

# 发布输出已通过 Desktop.csproj 自动带上根目录 libmpv-2.dll / yt-dlp.exe / cacert.crt 与 tools\ffmpeg\ffmpeg.exe；
# 这里只补齐缺失项，避免把 120MB+ 的 libmpv 重复打进 tools/ 导致成品体积翻倍。
Copy-Item (Join-Path $ToolsDir "cacert.crt") (Join-Path $OutToolsDir "cacert.crt") -Force
if (-not (Test-Path (Join-Path $OutToolsDir "ffmpeg\ffmpeg.exe"))) {
    Copy-Item (Join-Path $ToolsDir "ffmpeg\ffmpeg.exe") (Join-Path $OutToolsDir "ffmpeg\ffmpeg.exe") -Force
}

# 兼容程序直接从根目录加载 libmpv-2.dll / yt-dlp.exe 的场景
if (-not (Test-Path (Join-Path $PublishDir "libmpv-2.dll"))) {
    Copy-Item (Join-Path $ToolsDir "libmpv-2.dll") (Join-Path $PublishDir "libmpv-2.dll") -Force
}
if (-not (Test-Path (Join-Path $PublishDir "yt-dlp.exe"))) {
    Copy-Item (Join-Path $ToolsDir "yt-dlp.exe") (Join-Path $PublishDir "yt-dlp.exe") -Force
}

$LicensePath = Join-Path $RepoRoot "LICENSE"
if (Test-Path $LicensePath) {
    Copy-Item $LicensePath (Join-Path $PublishDir "LICENSE") -Force
}

# 生成面向体验用户的快速说明文件
$QuickStartContent = @"
LimoClip $Version ($Runtime 免环境绿色版)
==================================================

1. 启动方式
   请解压至非系统保护目录（如 D:\Tools\LimoClip\），双击运行 LimoClip.exe。
   若 Windows 弹出 SmartScreen 警告，点击「更多信息」->「仍要运行」即可。

2. 推荐初始配置
   - 右上角「账号与设置 -> 平台账号」中通过内置窗口登录 B站，可解锁原画画质与完整独轮车过滤。
   - 右上角「账号与设置 -> 语音识别（ASR）」中可配置硅基流动（国内免费额度）或 Groq（海外）API Key。

3. 开源协议与反馈
   - 本软件基于 GNU GPL-3.0 协议开源，内含 libmpv、FFmpeg、yt-dlp 等第三方开源组件。
   - 项目主页：https://heyiai02.github.io/LimoClip/
   - 问题反馈：https://github.com/heyiai02/LimoClip/issues
"@
Set-Content -Path (Join-Path $PublishDir "README_FIRST.txt") -Value $QuickStartContent -Encoding UTF8

# 清理可能残留的调试符号文件
Get-ChildItem -Path $PublishDir -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# 5. 压缩为 ZIP 并计算 SHA256
Write-Host "`n[4/5] 压缩生成 Release ZIP..." -ForegroundColor Yellow
Compress-Archive -Path $PublishDir -DestinationPath $ZipPath -CompressionLevel Optimal -Force

Write-Host "`n[5/5] 计算 SHA256 校验值..." -ForegroundColor Yellow
$Hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$ZipSizeMB = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 2)
"$Hash  $PackageName.zip" | Set-Content -Path $HashPath -Encoding ASCII

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "  打包完成！" -ForegroundColor Green
Write-Host "  文件: $ZipPath ($ZipSizeMB MB)" -ForegroundColor Green
Write-Host "  SHA256: $Hash" -ForegroundColor Green
Write-Host "  校验表: $HashPath" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
