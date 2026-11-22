# tools

运行时第三方二进制放这里，但**不提交到 Git**（`.gitignore` 已排除），以免仓库体积膨胀、也避开第三方二进制的再分发问题。唯一例外是 CA 包，播放/HTTPS 依赖它，所以跟踪在仓库里。

一条命令补齐（首次克隆后执行，已存在会跳过，`-Force` 重新下载）：

```powershell
powershell -ExecutionPolicy Bypass -File ..\scripts\fetch-tools.ps1
```

期望布局：

- `yt-dlp.exe` — Twitch / YouTube 流解析（`yt-dlp -j`）。上游：<https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe>
- `libmpv-2.dll` — `LibMpv.Avalonia` 加载的 libmpv 运行时。上游：<https://github.com/shinchiro/mpv-winbuild-cmake/releases> 里的 `mpv-dev-x86_64-*.7z`（取非 `-v3-` 版本，避免要求 AVX2）
- `ffmpeg/ffmpeg.exe` — 探测/兜底，可选，应用当前未直接调用。上游：<https://www.gyan.dev/ffmpeg/builds/>（essentials zip）
- `mpv/mpv.exe` 及其 DLL — 可选的独立 mpv 运行时（脚本不下载，需要时自行解包）
- `cacert.crt` — 传给 mpv 的 CA 根证书清单（仓库跟踪，约 229 KB）

应用按输出目录中的相对路径解析这些文件；发布时保持同样的相对布局即可。第三方许可各自归上游项目所有，正式分发（GitHub Releases 压缩包）时请一并附上对应许可证文本。
