[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$PluginId,

    [string]$Platform = 'YouTube',
    [string]$MinAppVersion = '0.0.0',
    [string]$ManifestPath = 'plugins\providers-manifest.json',
    [string]$DownloadUrl,
    [string]$PrivateKeyPath,
    [string]$PrivateKeyBase64,
    [string]$Configuration = 'Release',
    [string]$ArtifactsDirectory = 'artifacts\plugins',
    [string]$DllFileName
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($ProjectPath)) {
    $ProjectPath = Join-Path $repo $ProjectPath
}

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "插件工程不存在：$ProjectPath"
}

if ([string]::IsNullOrWhiteSpace($DllFileName)) {
    $DllFileName = 'plugin.dll'
}

$projectDir = Split-Path -Parent $ProjectPath
$lowerCfg = $Configuration.ToLowerInvariant()

[xml] $projectXml = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
$assemblyName = $null
$assemblyNodes = $projectXml.SelectNodes("//PropertyGroup/AssemblyName")
if ($null -ne $assemblyNodes -and $assemblyNodes.Count -gt 0) {
    $assemblyName = $assemblyNodes[0].InnerText.Trim()
}

if ([string]::IsNullOrWhiteSpace($assemblyName)) {
    $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
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

Write-Host "正在构建插件工程：$ProjectPath（$Configuration / $Version）" -ForegroundColor Cyan
& $dotnet build $ProjectPath -c $Configuration -p:Version=$Version -p:InformationalVersion=$Version --nologo
if ($LASTEXITCODE -ne 0) {
    throw "插件工程构建失败（退出码 $LASTEXITCODE）。"
}

$candidates = @(
    (Join-Path $projectDir "bin\$Configuration\net8.0\$assemblyName.dll"),
    (Join-Path $projectDir "bin\$Configuration\net8.0-windows\$assemblyName.dll")
)
$builtDll = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $builtDll) {
    $fallback = Get-ChildItem -Path (Join-Path $projectDir "bin\$Configuration") -Recurse -Filter "$assemblyName.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\ref\\' } |
        Select-Object -First 1
    if ($fallback) {
        $builtDll = $fallback.FullName
    }
}

if (-not $builtDll) {
    throw "找不到构建产物 $assemblyName.dll（已查找 bin\$Configuration\net8.0 等目录）。"
}

Write-Host "构建产物：$builtDll" -ForegroundColor Green

if (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {
    $ManifestPath = Join-Path $repo $ManifestPath
}

& (Join-Path $PSScriptRoot 'sign-plugin.ps1') `
    -DllPath $builtDll `
    -PluginId $PluginId `
    -Platform $Platform `
    -Version $Version `
    -MinAppVersion $MinAppVersion `
    -ManifestPath $ManifestPath `
    -DllFileName $DllFileName `
    -DownloadUrl $DownloadUrl `
    -PrivateKeyPath $PrivateKeyPath `
    -PrivateKeyBase64 $PrivateKeyBase64
if ($LASTEXITCODE -ne 0) {
    throw "插件签名步骤失败（退出码 $LASTEXITCODE）。"
}

# 发布用产物：把签名后的 DLL 规整命名为清单里的 dllName，便于上传到 Release 资产。
$artifactDir = Join-Path $repo $ArtifactsDirectory
$artifactPluginDir = Join-Path $artifactDir "$PluginId-$Version"
if (-not (Test-Path -LiteralPath $artifactPluginDir)) {
    New-Item -ItemType Directory -Path $artifactPluginDir -Force | Out-Null
}

$artifactDll = Join-Path $artifactPluginDir $DllFileName
Copy-Item -LiteralPath $builtDll -Destination $artifactDll -Force

Write-Host "签名后的插件已复制到：$artifactDll" -ForegroundColor Green
Write-Host "清单文件：$ManifestPath" -ForegroundColor Green
Write-Host ''
Write-Host '下一步：' -ForegroundColor Yellow
Write-Host "  1. 把 $artifactDll 上传到 GitHub Release 资产（下载地址与清单 downloadUrl 一致）。"
Write-Host '  2. 提交 plugins\providers-manifest.json，客户端「检查平台插件更新」即可发现新版本。'
