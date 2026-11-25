# Step 8 压测报告（2026-11-22）

> 归档说明：以下数据来自修复前的只读副本（HEAD `6e3c646` + 两处线程修复）；P0 已在 `a17bd3b` 正式修复并在本机复验可正常起播。原始文件位于 `.cache/step8/step8-report.md`。

## 1. 基线复述

- 分支 `main`，HEAD `6e3c6462637372afed5b2f381cdf969d1faab0f3`（`docs: add Step 8 handoff`），工作区干净，压测全程未改仓库代码（实验只在 `.cache\diag` 副本）。
- Build：0 错误 / 6 警告（均为既有警告：MSB3277 WindowsBase 冲突、CS9057 Dock 生成器、WFAC010、AVLN3001）。
- Test：**104/104 通过**（Data 9 + Platforms 38 + Desktop 57），16:04 本机复核。
- Step 8 验收（`livemonitor_v0_1.md` 1.2）：
  1. 6~9 路：两个 PaneId 集合一致的方案切换 10 次、相交主播原地复用零黑屏、换入换出只重启该路；2 小时挂机内存平稳；未开播 60s 轮询、断网恢复重连。
  2. 20~30 路：Compact15Fps + 音频常开静音 + 4M/8M/3M 缓存下，记录 CPU/内存/GPU Video Decode/Copy/3D 五项曲线与 UI 帧率，摸清硬解、显存回拷、UI 帧率拐点；音频若成瓶颈再评估 `aid=no`。
- 环境坑（本次实测）：
  - 多节点 MSBuild 在沙箱下静默失败，必须 `-m:1 -nodeReuse:false`。
  - `Start-Process <exe>` 静默挂起不建进程，必须 `cmd /c start "" "<exe>"`。
  - testhost 在 workspace-write 下 `Win32Exception(5)`，切 danger-full-access 后正常（本次 104/104 即在此模式跑出）。
  - `Get-CimInstance Win32_PerfFormattedData_*` 会卡数分钟；采样改用进程 `TotalProcessorTime` 差分。
  - PresentMon 起 trace session 被拒（高完整性 + SYSTEM + 提权均失败），UI 帧率改用 Avalonia `RequestAnimationFrame` 探针。
  - `tools\mpv\mpv.com` 直接播流报 TLS CA 失败，属 CLI 现象；应用侧 `tls-ca-file` 正常。

## 2. P0 阻塞缺陷（HEAD 6e3c646 必现，已在 a17bd3b 修复）

任何含在线房间的方案在启动时弹「启动失败」，`LoadProfileAsync` 整体中止，**当前提交无法起播任何方案**，Step 8 验收被硬阻塞。

```
System.InvalidOperationException: Call from invalid thread
  at Avalonia.Threading.Dispatcher.VerifyAccess()
  at Avalonia.AvaloniaObject.SetValue[T](...)
  at Avalonia.Controls.ContentControl.set_Content(...)
  at LiveMonitor.Desktop.Player.MpvPlayerView.SetCompactMode(Boolean enabled)
       MpvPlayerView.axaml.cs:line 287   // CompactButton.Content = ...
  at LiveMonitor.Desktop.Services.WorkspacePlayerManager.StartAsync(...)
       WorkspacePlayerManager.cs:line 174
  at ...MainWindowViewModel.LoadProfileAsync(...) MainWindowViewModel.cs:1431
  at ...MainWindowViewModel.InitializeAsync(...)  MainWindowViewModel.cs:276
  at ...MainWindow.OnOpened(...)                  MainWindow.axaml.cs:134
```

根因：`WorkspacePlayerManager.StartAsync` 在 `WorkspacePlayerManager.cs:143/147` 用了 `.ConfigureAwait(false)`，续体回到线程池；`:174` 的 `SetCompactMode` 直接改 `CompactButton.Content`（UI 线程约束）。catch 里 `ShowFailed`/`ShowOffline` 同样在线程池碰 UI，二次异常把整次方案加载打断。

最小修（建议，未实施）：去掉 `:143`、`:147` 两处 `.ConfigureAwait(false)`（续体回 UI 线程），或把 `:174`/catch 的 UI 调用 `Dispatcher.UIThread.Post`。

## 3. 压测环境与方法

- i7-12700K（12C/20T）、31.75GB、RTX 3060 Ti（驱动 32.0.15.8180）、Win11 22000、1920×1080。
- 客户端：`.cache\diag` = HEAD + 上述两处 `ConfigureAwait(false)` 移除 + RAF 帧率探针（仓库代码零改动）。
- 房间：B 站游客态 720P（`current_qn=250`，接口只返回单档），全部 `DefaultMuted=1`、非焦点；≥12 路自动 compact（`vf=fps=15`）。
- 库：`LIVEMONITOR_DB=.cache\step8\db\benchN.db`，单方案 N 个 `pane-1..N`（6/9=3×3，20/25=5×5，30=6×6）。
- 采样：启动→最大化→settle 45~60s→15 个样本（约 4s 间隔），进程 CPU 差分、`WorkingSet64`、`\GPU Engine(pid_*)\Utilization Percentage` 分 `videodecode/copy/3d`、`\GPU Process Memory`、`nvidia-smi`、`uifps.log`。

## 4. 实测数据（15 样本均值，720P，compact 15fps）

| 路数 | CPU(%整机) | 工作集(MB, 趋势) | VideoDecode | Copy | 3D | nvGPU | nvDec | 进程显存(MB) | UI fps | 起播失败 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 0.8~11* | 299→331 | 2.0 | 0.0 | 1.0 | 6 | 1~2 | 181 | 30 | 0 |
| 6 | 3.0~3.6 | 865→933 | 15.4 | 0.04 | 4.5 | 11.7 | 11.9 | 781 | 23.0 | 0 |
| 9 | 5.5~6.8 | 1195→1299 | 23.1 | 0.08 | 7.0 | 16.3 | 21.5 | 1085 | 20.9 | 0 |
| 20 | 13~15 | 2261→2489 | 43.6 | 0.14 | 12.8 | 28.5 | 39.5 | 1912 | 22.4 | 0 |
| 25 | 24~33 | 2713→3016 | 67.2 | 0.20 | 17.4 | 38.8 | 66.6 | 2352 | 24.5 | 0 |
| 30 | 28~32 | 3240→3608 | 60.7 | 0.22 | 20.4 | 38.8 | 57.5 | 2867 | 24.5 | 0 |

\* 单路首样本含启动突发，稳态约 0.8~1%。

每路边际成本：NVDEC ≈ 2.0~2.7%，CPU ≈ 0.8~1.5%（含音量表），进程显存 ≈ 95~123MB，工作集约 110~130MB。

观察：
- `copy` 引擎全程 ≤0.22%，**没有可测量的显存回拷瓶颈**；即便 `hwdec-current=d3d11va-copy`，回拷开销主要落在 3D/内存带宽而非 Copy 引擎。
- `videoencode` 全程 0；`nvDec` 与 VideoDecode 曲线一致。
- UI fps 稳定 20~25，未随路数坍塌（该值跟随视频呈现帧率，compact 下不是 60Hz 上限，故不是有效“卡顿”指标）。
- 25 路 decode 67% 高于 30 路 61%，与线性预期不符（30 路 6×6 更小窗格、码率差异、计数器采样均可能），需复测确认；两档都在 NVDEC 60~70% 区间。

## 5. aid=no 三组 A/B（30 路，同一批房间）

| 变体 | 音频 | 音量表 af | CPU(%整机) |
|---|---|---|---|
| A 现状 | `aid=auto` + `mute=yes` | astats→file | 30.5 |
| C 只关表 | `aid=auto` + `mute=yes` | 无 | 15.2 |
| B 回退 | `aid=no` | 无 | 12.5 |

- 音量表（每路 `af=lavfi=[astats...,file=...:direct=1]`）≈ **15 个百分点**整机 CPU（约 3 核），是 30 路 CPU 的最大单项。
- 音频解码本身（AAC 30 路同时解）≈ **2.7 个百分点**（约 0.55 核）。
- **结论：不建议为性能回退 `aid=no`。** 只能省约 3% 整机，却会牺牲焦点开声体验（官方注释已说明 `aid=no→auto` 需重建音频输出，易出现「点开声没声」），不解决真正的开销来源。
- 真正该做的是**让音量表只在该路可听/聚焦时运行**（或复用已有电平数据），30 路可省 ~15% 整机，且完全不动音频语义。

## 6. 瓶颈排序

1. **NVDEC 硬解**：25 路已 67%，30 路 61~67%，是第一个接近饱和的硬件单元；40 路预计撞上限。
2. **音频电平表**：15% 整机（软件 CPU），与路数线性，属实现层浪费。
3. **内存**：短窗内线性增长，30 路约 4.5~5MB/s（约 0.17MB/s/路），2 小时量级会撞 32GB；尚不能区分真泄漏、mpv 8M/路缓存填充与帧率探针强制渲染的影响，**必须做 2h 挂机确认**。
4. **Copy/3D**：Copy 可忽略；3D 20%（30 路）随合成路数线性，仍有富余。
5. **UI 帧率**：20~25fps 稳定，不是瓶颈。

## 7. 建议

1. 先修 P0（两行），否则 Step 8 任何验收都无法在 HEAD 上复现。
2. 音频电平表按需启用（聚焦/非静音才挂 `af`），预期 30 路省 15% 整机 CPU；比回退 `aid=no` 收益大一个数量级。
3. 30 路可用但 NVDEC 已到 60~70%，建议把「30 路」定义为上限档、40 路需另做降载（降分辨率/降帧率到解码前，如 `vf` 之前的选择或换低码率档）。
4. 补 2 小时挂机（含无探针版本）确认内存曲线，再判 Step 8 第 1 条“内存平稳”。
5. 6~9 路方案切换 10 次、断网重连、未开播轮询本轮未覆盖（按本次范围只做基线与压测）。
