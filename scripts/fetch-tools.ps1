<#
.SYNOPSIS
    下载 LiveMonitor 运行时需要的第三方二进制到 tools/。

.DESCRIPTION
    仓库只跟踪源码和 tools/cacert.crt；下列二进制体积大、且再分发涉及第三方许可，
    因此不进 Git（见 .gitignore），首次克隆后用本脚本补齐：

      tools/libmpv-2.dll      mpv 播放内核，LibMpv.Avalonia 加载（shinchiro 构建的 mpv-dev-x86_64）
      tools/yt-dlp.exe        Twitch / YouTube 流解析
      tools/cacert.crt        CA 根证书清单（仓库已带，缺失时才下载）
      tools/ffmpeg/ffmpeg.exe 可选（-WithFfmpeg），应用当前未直接调用，仅作探测/兜底

    已存在的文件默认跳过；加 -Force 重新下载。解压 7z 时优先用系统已装的 7-Zip，
    找不到就临时下载官方单文件 7zr.exe，不写注册表、不装软件。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1 -WithFfmpeg

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/fetch-tools.ps1 -Force -ToolsDir D:\build\tools
#>
[CmdletBinding()]
param(
    # 目标目录，默认仓库根目录下的 tools/
    [string]$ToolsDir = (Join-Path $PSScriptRoot '..\tools'),

    # 额外下载 ffmpeg.exe（约 114 MB 压缩包）
    [switch]$WithFfmpeg,

    # 已存在也重新下载
    [switch]$Force,

    # 覆盖 libmpv 压缩包地址（默认取 shinchiro 最新 mpv-dev-x86_64）
    [string]$MpvDevUrl
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$MpvDevFallbackUrl = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261111/mpv-dev-x86_64-20261111-git-bcde6b93cc.7z'
$YtDlpUrl          = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
$CacertUrl         = 'https://curl.se/ca/cacert.pem'
$FfmpegUrl         = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'

# 支持相对路径（相对当前 PowerShell 位置）与绝对路径
$ToolsDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ToolsDir)
if (-not (Test-Path -LiteralPath $ToolsDir)) {
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
}
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('livemonitor-tools-' + [guid]::NewGuid().ToString('N').Substring(0, 8))

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Skip([string]$Path) { Write-Host "    已存在，跳过：$Path（-Force 可重新下载）" -ForegroundColor DarkGray }

function Get-RemoteFile {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$OutFile
    )
    Write-Host "    下载 $Url"
    $part = "$OutFile.part"
    if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
    Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
    Move-Item -LiteralPath $part -Destination $OutFile -Force
}

function Get-SevenZipExe {
    foreach ($name in @('7z.exe', '7za.exe', '7zr.exe')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    foreach ($path in @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }
    $local = Join-Path $WorkDir '7zr.exe'
    if (-not (Test-Path -LiteralPath $local)) {
        Write-Host '    未找到 7-Zip，临时下载官方 7zr.exe'
        Get-RemoteFile 'https://www.7-zip.org/a/7zr.exe' $local
    }
    return $local
}

function Expand-SevenZip {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )
    $exe = Get-SevenZipExe
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & $exe x $Archive "-o$Destination" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "解压失败（7-Zip 退出码 $LASTEXITCODE）：$Archive"
    }
}

function Resolve-MpvDevUrl {
    if ($MpvDevUrl) { return $MpvDevUrl }
    try {
        $headers = @{ 'User-Agent' = 'LiveMonitor-fetch-tools'; 'Accept' = 'application/vnd.github+json' }
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' -Headers $headers
        $asset = $release.assets |
            Where-Object { $_.name -like 'mpv-dev-x86_64-*.7z' -and $_.name -notlike '*-v3-*' } |
            Select-Object -First 1
        if ($asset) { return $asset.browser_download_url }
    } catch {
        Write-Warning "查询 GitHub 最新版失败（$($_.Exception.Message)），改用固定版本"
    }
    return $MpvDevFallbackUrl
}

try {
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    Write-Host "目标目录：$ToolsDir"

    # 1) libmpv-2.dll
    Write-Step 'libmpv-2.dll（mpv 播放内核）'
    $libmpv = Join-Path $ToolsDir 'libmpv-2.dll'
    if ((Test-Path -LiteralPath $libmpv) -and -not $Force) {
        Write-Skip $libmpv
    } else {
        $archive = Join-Path $WorkDir 'mpv-dev.7z'
        Get-RemoteFile (Resolve-MpvDevUrl) $archive
        $extracted = Join-Path $WorkDir 'mpv-dev'
        Expand-SevenZip $archive $extracted
        $dll = Get-ChildItem -LiteralPath $extracted -Recurse -Filter 'libmpv-2.dll' -File | Select-Object -First 1
        if (-not $dll) { throw '压缩包里没有找到 libmpv-2.dll' }
        Copy-Item -LiteralPath $dll.FullName -Destination $libmpv -Force
        Write-Host ("    完成：{0:N1} MB" -f ((Get-Item -LiteralPath $libmpv).Length / 1MB)) -ForegroundColor Green
    }

    # 2) yt-dlp.exe
    Write-Step 'yt-dlp.exe（Twitch / YouTube 流解析）'
    $ytdlp = Join-Path $ToolsDir 'yt-dlp.exe'
    if ((Test-Path -LiteralPath $ytdlp) -and -not $Force) {
        Write-Skip $ytdlp
    } else {
        Get-RemoteFile $YtDlpUrl $ytdlp
        Write-Host ("    完成：{0:N1} MB" -f ((Get-Item -LiteralPath $ytdlp).Length / 1MB)) -ForegroundColor Green
    }

    # 3) cacert.crt（仓库通常已带）
    Write-Step 'cacert.crt（CA 根证书清单）'
    $cacert = Join-Path $ToolsDir 'cacert.crt'
    if ((Test-Path -LiteralPath $cacert) -and -not $Force) {
        Write-Skip $cacert
    } else {
        Get-RemoteFile $CacertUrl $cacert
        Write-Host '    完成' -ForegroundColor Green
    }

    # 4) ffmpeg.exe（可选）
    if ($WithFfmpeg) {
        Write-Step 'ffmpeg.exe（可选）'
        $ffmpegDir = Join-Path $ToolsDir 'ffmpeg'
        $ffmpeg = Join-Path $ffmpegDir 'ffmpeg.exe'
        if ((Test-Path -LiteralPath $ffmpeg) -and -not $Force) {
            Write-Skip $ffmpeg
        } else {
            $zip = Join-Path $WorkDir 'ffmpeg.zip'
            Get-RemoteFile $FfmpegUrl $zip
            $unzipped = Join-Path $WorkDir 'ffmpeg'
            Expand-Archive -LiteralPath $zip -DestinationPath $unzipped -Force
            New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
            $exe = Get-ChildItem -LiteralPath $unzipped -Recurse -Filter 'ffmpeg.exe' -File | Select-Object -First 1
            if (-not $exe) { throw '压缩包里没有找到 ffmpeg.exe' }
            Copy-Item -LiteralPath $exe.FullName -Destination $ffmpeg -Force
            $probe = Get-ChildItem -LiteralPath $unzipped -Recurse -Filter 'ffprobe.exe' -File | Select-Object -First 1
            if ($probe) { Copy-Item -LiteralPath $probe.FullName -Destination (Join-Path $ffmpegDir 'ffprobe.exe') -Force }
            Write-Host ("    完成：{0:N1} MB" -f ((Get-Item -LiteralPath $ffmpeg).Length / 1MB)) -ForegroundColor Green
        }
    }

    Write-Host ''
    Write-Host 'tools/ 当前内容：' -ForegroundColor Green
    Get-ChildItem -LiteralPath $ToolsDir -Recurse -File |
        Where-Object { $_.Extension -in @('.dll', '.exe', '.crt', '.pem') } |
        Sort-Object FullName |
        ForEach-Object {
            '    {0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.FullName.Substring($ToolsDir.Length + 1)
        }
} finally {
    if (Test-Path -LiteralPath $WorkDir) {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
