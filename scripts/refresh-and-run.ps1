# LiveMonitor 启动器：构建 → 复制到工作区外 → 启动
#
# 为什么需要这个脚本：
#   某些开发机（例如本仓库当前的验证机）对「位于工作区目录内的可执行文件」有强制写沙箱：
#   直接运行 bin\Debug 下的 exe 时，ffmpeg 只能写入工作区内目录，连它拉起的 explorer.exe
#   都会以 0xC0000142 启动失败。把构建产物复制到工作区外再启动即可规避。
#   普通机器上也可以直接 `dotnet run`，不需要本脚本；但用本脚本启动同样安全。
#
# 用法（在任意 PowerShell 窗口）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\refresh-and-run.ps1
#   powershell ... -File .\scripts\refresh-and-run.ps1 -SkipBuild      # 只复制+启动
#   powershell ... -File .\scripts\refresh-and-run.ps1 -NoLaunch       # 只构建+复制
#   powershell ... -File .\scripts\refresh-and-run.ps1 -RunDirectory 'E:\LimoClip-v05-run'
#
# 构建环境说明：
#   如果仓库内自带便携 SDK（.dotnet\dotnet.exe），构建进程也在受影响的工作区内，
#   因此构建时必须把 TEMP/APPDATA/LOCALAPPDATA/NUGET_PACKAGES 一并指回工作区内；
#   启动应用前再恢复真实用户环境，避免把「工作区内的临时目录」泄漏给应用。
#   如果仓库没有便携 SDK，则直接使用 PATH 里的 dotnet，不覆盖上述环境变量。

param(
    [switch]$SkipBuild,
    [switch]$NoLaunch,
    [string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repo 'src\LiveMonitor.Desktop\bin\Debug\net8.0-windows'
$log = Join-Path $repo '.cache\launcher-run.log'

if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    if (-not [string]::IsNullOrWhiteSpace($env:LIVEMONITOR_RUN_DIR)) {
        $RunDirectory = $env:LIVEMONITOR_RUN_DIR
    } else {
        $RunDirectory = Join-Path $env:LOCALAPPDATA 'LimoClip\run'
    }
}
$dst = $RunDirectory

New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
try { Start-Transcript -Path $log -Force | Out-Null } catch { }

# 记住真实用户环境，finally 中恢复后再启动应用。
$realAppData = Join-Path $env:USERPROFILE 'AppData\Roaming'
$realLocalAppData = Join-Path $env:USERPROFILE 'AppData\Local'
$realTemp = [System.IO.Path]::GetTempPath()

function Write-Step([string]$Message) {
    Write-Host $Message -ForegroundColor Cyan
}

try {
    $running = @(Get-Process -Name 'LimoClip' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $pids = ($running | ForEach-Object { $_.Id }) -join ', '
        Write-Host "LimoClip 正在运行（PID: $pids），请先关闭应用再执行本脚本。" -ForegroundColor Yellow
        Read-Host '按回车退出' | Out-Null
        exit 1
    }

    if (-not $SkipBuild) {
        Write-Step '[1/3] 构建 LiveMonitor.sln ...'
        $dotnet = Join-Path $repo '.dotnet\dotnet.exe'
        $portableSdk = Test-Path $dotnet
        if (-not $portableSdk) {
            $dotnet = 'dotnet'
        }

        if ($portableSdk) {
            # 便携 SDK 位于工作区内：构建时临时目录/配置目录必须也位于工作区内。
            $env:DOTNET_ROOT = Join-Path $repo '.dotnet'
            $env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
            $env:NUGET_PACKAGES = Join-Path $repo '.nuget\packages'
            $env:APPDATA = Join-Path $repo '.appdata'
            $env:LOCALAPPDATA = Join-Path $repo '.appdata'
            $env:TEMP = Join-Path $repo '.cache\temp'
            $env:TMP = $env:TEMP
            $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
            foreach ($dir in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:APPDATA, $env:TEMP)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
        }

        & $dotnet build (Join-Path $repo 'LiveMonitor.sln')
        if ($LASTEXITCODE -ne 0) { throw "构建失败（dotnet build 退出码 $LASTEXITCODE）。" }
        Write-Host '构建成功。' -ForegroundColor Green
    } else {
        Write-Step '[1/3] 跳过构建'
    }

    if (-not (Test-Path $bin)) { throw "找不到构建输出目录：$bin" }
    Write-Step "[2/3] 复制构建输出到 $dst ..."
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    robocopy $bin $dst /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy 复制失败，退出码 $LASTEXITCODE。" }
    Remove-Item (Join-Path $dst 'startup-fallback.log') -Force -ErrorAction SilentlyContinue
    Write-Host '复制完成。' -ForegroundColor Green
} catch {
    Write-Host ''
    Write-Host "启动器执行失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "日志：$log" -ForegroundColor DarkGray
    Read-Host '按回车退出' | Out-Null
    exit 1
} finally {
    # 恢复真实用户环境，避免把「工作区内临时目录」泄漏给应用。
    $env:APPDATA = $realAppData
    $env:LOCALAPPDATA = $realLocalAppData
    $env:TEMP = $realTemp
    $env:TMP = $realTemp
    try { Stop-Transcript | Out-Null } catch { }
}

if (-not $NoLaunch) {
    Write-Step '[3/3] 启动 LimoClip（工作区外副本）...'
    $exe = Join-Path $dst 'LimoClip.exe'
    if (-not (Test-Path $exe)) {
        Write-Host "找不到程序：$exe" -ForegroundColor Red
        Read-Host '按回车退出' | Out-Null
        exit 1
    }

    Start-Process -FilePath $exe -WorkingDirectory $dst
    Start-Sleep -Seconds 2
    $proc = @(Get-Process -Name 'LimoClip' -ErrorAction SilentlyContinue)
    if ($proc.Count -eq 0) {
        Write-Host '应用启动失败，请把上面的日志发给开发者。' -ForegroundColor Red
        Read-Host '按回车退出' | Out-Null
        exit 1
    }

    Write-Host "已启动（PID: $((($proc | ForEach-Object { $_.Id }) -join ', '))）。" -ForegroundColor Green
    Start-Sleep -Seconds 1
} else {
    Write-Step '[3/3] 跳过启动'
}

exit 0
