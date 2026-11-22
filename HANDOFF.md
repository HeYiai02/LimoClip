# LiveMonitor Step 8 交接

## 基线

- 仓库：`E:\code\liveSurveilance`，分支 `main`，代码基线 `1118197`（重连退避 + 音量表按需挂载）；前序 P0 修复 `a17bd3b`，报告/交接 `1d10bd8`、`f9836dd`，本文件更新 `9159ffb`。
- 文档：`livemonitor_v0_1.md` v1.7（本期契约）、`code_artifact.md` v1.3（长期架构）。
- 状态：`dotnet build LiveMonitor.sln` 0 错误；测试 105/105（Platforms 38、Desktop 58、Data 9）。
- 运行数据：默认库未动；测试库在 `.cache/step6-test8.db`，应用当前已关闭。
- 第三方二进制：`tools/` 下的 `yt-dlp.exe`、`libmpv-2.dll`、`ffmpeg/`、`mpv/` 被 `.gitignore` 排除，按 `tools/README.md` 本地准备；`tools/cacert.crt` 已入库。

## P0 修复

`WorkspacePlayerManager.StartAsync` 的两处 `ConfigureAwait(false)` 已移除，续体回到 UI 线程；`RetryAsync` 在 `ReconnectRequested` 后改为 `Dispatcher.UIThread.InvokeAsync` 再启动。已提交 `a17bd3b`；build 0，105/105，并用 `.cache/step6-test8.db` 启动复验，B站/Twitch 双流正常。

## 本轮增量（1118197）

- 断网/起播失败：`ScheduleRetry` 在每次 `StartAsync` 失败后继续排 3s→10s→30s；3 次后进入 `Failed`，60 秒复检（`ScheduleFailureRecheck` + `ScheduleOfflinePoll(force:true)`），`Playing` 时取消重试/复检。
- 音量表：`MpvPlayerView` 仅在 `!_muted && _audioEnabled` 时挂 `astats`，静音 `af clr` 摘除；音频仍 `aid=auto`，未回退 `aid=no`。
- 真实断网恢复尚未复测；当前只验证了编译、105 测试和手动开声/静音 smoke。

## 已有 Step 8 数据

- 只读副本实测报告见 `STEP8_REPORT.md`（原始数据 `.cache/step8/`）；P0 修复前完成，修复的副本只用于测量。
- 关键结论：20/25/30 路可起播；30 路 CPU 28~32%、工作集 3.2~3.6GB、VideoDecode 60.7%、Copy 0.22%、3D 20.4%，UI fps 20~25。
- 最先逼近上限的是 NVDEC（60~70%），不是 Copy/3D；不建议回退 `aid=no`（只省约 2.7% 整机，牺牲焦点开声）。
- 30 路 CPU 的最大单项是每路音量表 `astats`（约 15 个百分点）；已在 `1118197` 改为按需挂载，待复测确认 CPU 回落。
- 内存短窗线性增长（30 路约 4.5~5MB/s），2 小时挂机尚未做，暂不能判定泄漏/缓存填充/探针影响。

## Step 8 目标

按 `livemonitor_v0_1.md` 1.2：

1. **常规 6~9 路**
   - 建两个 **PaneId 集合一致**的方案，反复切换 10 次：相交主播原地复用、零黑屏；换入/换出的窗格只重启该路。
   - 连续挂机 2 小时，内存曲线平稳、无明显泄漏。
   - 未开播 60 秒轮询；断网恢复后自动重连。
2. **极限 20~30 路**
   - `Compact15Fps`（`vf=fps=15`）+ 音频常开但静音（`aid=auto` + `mute=yes`）+ `demuxer-max-bytes=4M`（高清 8M / 低清 3M）。
   - 记录 **CPU、内存、GPU Video Decode、GPU Copy、GPU 3D** 五项曲线，测出本机硬解、显存回拷与 UI 帧率的真实拐点。
   - 若音频解码成为瓶颈，再评估是否退回 `aid=no`。

## 约束

- 平台逻辑保持现状：B站原生 API、Twitch `yt-dlp -j`；**不要**换 streamlink，**不要**尝试剔除 Twitch SSAI 广告。
- 焦点放大、音频逻辑、`PlatformCredentials`、`PlatformType` 保持现状。
- 不重构、不顺手改 UI；Step 8 先测基线，再调参。
- 弹幕是后续功能：`CreateDanmakuClient` 留在 V0.2 计划；`LayoutSlot.DefaultDanmakuVisible` 与 `RoomTileViewModel.IsDanmakuVisible` 已在代码里，但底部按钮/完整 UI 尚未接完，不要把它当成 Step 8 的阻塞项。

## 环境

便携 SDK 在 `.dotnet`，构建前设置：

```powershell
$root = 'E:\code\liveSurveilance'
$env:DOTNET_ROOT = Join-Path $root '.dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:APPDATA = Join-Path $root '.appdata'
$env:LOCALAPPDATA = Join-Path $root '.appdata'
$env:TEMP = Join-Path $root '.cache\temp'
$env:TMP = $env:TEMP
```

构建 / 测试：

```powershell
& "$root\.dotnet\dotnet.exe" build "$root\LiveMonitor.sln" --nologo -v minimal
& "$root\.dotnet\dotnet.exe" test "$root\LiveMonitor.sln" --no-build --nologo -v minimal
```

已知坑：

- `.NET HTTPS` 在 `workspace-write` 下可能报 `SEC_E_NO_CREDENTIALS`；需要联网还原时用 `danger-full-access`。
- 直接 `Start-Process` 启动 GUI，不重定向输出；输出重定向到文件会导致进程异常。
- 截图/临时 DB/测试产物都放 `.cache/`；不要提交。
- `dotnet test` 的 `VSTEST_ARTIFACTS_DIRECTORY` 提示是旧 Test.Sdk 的噪音，不影响通过结果。

## 第一步建议

1. 先读 `livemonitor_v0_1.md` 1.2、Step 8、`STEP8_REPORT.md` 和 `code_artifact.md` 的性能/播放器章节。
2. 检查 `git status` 干净、commit 与本文一致；跑一次 build/test 确认 105/105。
3. 不要重跑已覆盖的 1/6/9/20/25/30 路基础表；优先补：
   - 6~9 路两个 PaneId 方案切换 10 次、断网重连、未开播轮询；
   - 2 小时挂机（无帧率探针），确认内存是泄漏还是缓存；
   - 验证已实现的音量表按需挂载：静音时无 `astats`、开声恢复、30 路 CPU 是否回落到接近“无表”水平。
4. 30 路作为上限档；40 路需在解码前降载（当前 `vf=fps=15` 在解码后，降不了 NVDEC）。
5. 只输出测量、瓶颈、建议；未经确认不要改播放器默认参数。

## 新 session 首条消息（可直接复制）

> 你在 `E:\code\liveSurveilance`。先读 `HANDOFF.md`、`STEP8_REPORT.md`、`livemonitor_v0_1.md`、`code_artifact.md`，不要改代码。复述当前 commit、build/test 状态和已有 Step 8 数据；不要重跑已覆盖的 1/6/9/20/25/30 路基础表。确认后优先补 6~9 路方案切换/断网重连、2 小时无探针挂机内存，以及验证音量表按需挂载后的 30 路 CPU。不要重构，不要改平台/焦点/音频语义，不要为性能回退 `aid=no`。
