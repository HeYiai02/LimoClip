[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DllPath,

    [Parameter(Mandatory = $true)]
    [string]$PluginId,

    [Parameter(Mandatory = $true)]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$MinAppVersion = '0.0.0',
    [string]$DllFileName,
    [string]$DownloadUrl,
    [string]$PrivateKeyBase64,
    [string]$PrivateKeyPath,
    [string]$ManifestPath,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$signerProject = Join-Path $repo 'scripts\PluginSigner\PluginSigner.csproj'

if (-not (Test-Path -LiteralPath $DllPath)) {
    throw "插件 DLL 不存在：$DllPath"
}

if (-not (Test-Path -LiteralPath $signerProject)) {
    throw "找不到签名工具工程：$signerProject"
}

# 私钥只允许来自参数或环境变量，脚本不落盘、不回显。
$keyBase64 = $PrivateKeyBase64
if ([string]::IsNullOrWhiteSpace($keyBase64)) {
    $keyBase64 = $env:LIVEMONITOR_PLUGIN_KEY
}

if ([string]::IsNullOrWhiteSpace($keyBase64) -and -not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    if (-not (Test-Path -LiteralPath $PrivateKeyPath)) {
        throw "私钥文件不存在：$PrivateKeyPath"
    }

    $keyBase64 = (Get-Content -LiteralPath $PrivateKeyPath -Raw -Encoding UTF8).Trim()
}

if ([string]::IsNullOrWhiteSpace($keyBase64)) {
    throw '缺少签名私钥：请用 -PrivateKeyPath / -PrivateKeyBase64 或环境变量 LIVEMONITOR_PLUGIN_KEY 提供 Ed25519 私钥（32 字节 seed 的 Base64）。'
}

$dotnet = Join-Path $repo '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = 'dotnet'
}

$env:DOTNET_ROOT = Join-Path $repo '.dotnet'
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget\packages'
$env:APPDATA = Join-Path $repo '.appdata'
$env:LOCALAPPDATA = Join-Path $repo '.appdata'
$env:TMP = Join-Path $repo '.cache\temp'
$env:TEMP = $env:TMP
foreach ($dir in @($env:DOTNET_CLI_HOME, $env:APPDATA, $env:TMP)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

$signArgs = @(
    'run', '--project', $signerProject, '-c', 'Release', '--', 'sign',
    '--dll', (Resolve-Path -LiteralPath $DllPath).Path,
    '--plugin-id', $PluginId,
    '--platform', $Platform,
    '--version', $Version,
    '--min-app-version', $MinAppVersion,
    '--key-base64', $keyBase64
)

if (-not [string]::IsNullOrWhiteSpace($DllFileName)) {
    $signArgs += @('--dll-name', $DllFileName)
}

if (-not [string]::IsNullOrWhiteSpace($DownloadUrl)) {
    $signArgs += @('--download-url', $DownloadUrl)
}

if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $manifestFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ManifestPath)
    $signArgs += @('--manifest', $manifestFull)
}

if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
    $outFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
    $signArgs += @('--out', $outFull)
}

Write-Host "正在签名插件：$([System.IO.Path]::GetFileName($DllPath))（$PluginId / $Platform / $Version）" -ForegroundColor Cyan
& $dotnet @signArgs | ForEach-Object { Write-Host $_ }

if ($LASTEXITCODE -ne 0) {
    throw "插件签名失败（退出码 $LASTEXITCODE）。"
}

if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    Write-Host "清单已更新：$(Resolve-Path -LiteralPath $ManifestPath -ErrorAction SilentlyContinue)" -ForegroundColor Green
}

Write-Host '签名完成。' -ForegroundColor Green
