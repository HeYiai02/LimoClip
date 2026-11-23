# LiveMonitor V0.2 交接（弹幕引擎与高能空跑）

## 状态

- 仓库：`E:\code\liveSurveilance`，分支 `main`；V0.2 改动**尚未提交**（全部在工作区，上次提交 `d60477b`）。
- 构建：`dotnet build LiveMonitor.sln` 0 错误；已知警告仅 `MSB3277`（WebView2/WPF WindowsBase 冲突）、`CS9057`（Dock 分析器版本）、`WFAC010`、`AVLN3001`。
- 测试：**281/281 通过** —— Data 21、Platforms 114、Danmaku 88、Desktop 58。
- 工程：新增 `src/LiveMonitor.Danmaku`、`tests/LiveMonitor.Danmaku.Tests`，解决方案现 **12 个工程**。
- 文档：`code_artifact.md` v1.4（长期架构）、`limoclip_v0_2.md`（V0.2 设计与实施记录）、`STEP8_REPORT.md`（V0.1 性能基线，仍是当前唯一整机压测数据）。
- 第三方二进制：`tools/` 下 `libmpv-2.dll`、`yt-dlp.exe`、`ffmpeg/`、`mpv/` 仍被 `.gitignore` 排除，按 `tools/README.md` 准备；`tools/cacert.crt` 已入库。

## V0.2 已实现内容

### Core（新增契约，不动既有 API）

- Models：`DanmakuMessage`（Timestamp 约定为**客户端本地接收时间**）、`HighlightEvent`、`HighlightRuleOptions`（含 `Normalize()` 夹取与 6 个默认值）。
- Interfaces：`IDanmakuClient`、`IDanmakuClientFactory`、`IDanmakuFilter`、`IHighlightDetector`、`IHighlightEventRepository`。
- `AppSettingKeys` 追加 6 键：`HighlightShortWindowSeconds` / `HighlightBaselineWindowSeconds` / `HighlightDensityMultiplier` / `HighlightMinShortWindowCount` / `HighlightCooldownSeconds` / `HighlightBoostKeywords`。

### Platforms（弹幕客户端）

- `LiveMonitor.Platforms.Bilibili`：`BilibiliPacketParser`（纯函数，16B 大端头、zlib/brotli、递归解包、DANMU_MSG/SEND_GIFT/SUPER_CHAT_MESSAGE、op7 认证包、op2 心跳包）、`BilibiliWbiSigner`（WBI mixin 置换 + `w_rid` 签名；`getDanmuInfo` 自 2026-11 起必须签名，否则返回 `-352`，客户端缓存 `nav` 的 img/sub key 1 小时）、`IBilibiliWebSocketConnection` + `ClientWebSocketBilibiliConnection`、`BilibiliDanmakuClient`（nav 取密钥 → getDanmuInfo → wss /sub → 认证 → 30s 心跳；单次连接失败即抛，不重连）。
- `LiveMonitor.Platforms.Twitch`：`TwitchIrcParser`、`ITwitchIrcConnection` + `ClientWebSocketTwitchConnection`、`TwitchDanmakuClient`（匿名只读 IRC：CAP/PASS/NICK/JOIN、PING/PONG、RECONNECT、多行帧、IRCv3 tags）。
- `LiveMonitor.Platforms.DanmakuClientFactory : IDanmakuClientFactory`（Bilibili / Twitch，其他平台返回 null）。

### Danmaku（新工程）

- `Filters/KeywordFilter`：包含匹配 + 正则（`Compiled | IgnoreCase | CultureInvariant`，100ms 超时）；非法正则跳过并记日志；可热更新。
- `Filters/BilibiliUnicycleFilter`：≤6 字短情绪词与游客 uid=0 放行、10 分钟静默、30s 长文本 / 15s 完全一致 / 60s 机械复读、15 分钟无发言状态驱逐。
- `Analyzers/SlidingWindowDetector`：按房间 1 秒环形桶，加权短窗/基线、绝对条数下限、冷却、`666/草/神回/Pog` +0.5 权重。
- `HighlightSettingsStore`：通过 `IAppSettingsRepository` 读写高能参数（InvariantCulture，非法值回落默认）。
- `DanmakuManager`：按 `(Platform, RoomId)` 引用计数共享连接；Acquire 返回 `DanmakuSessionHandle`（批量 50ms `MessagesReceived`）；管线 = KeywordFilter → BilibiliUnicycleFilter → SlidingWindowDetector → 批量渲染；高能事件事件广播 + 异步落盘；退避 3s→10s→30s→60s（稳定 30s 重置），可注入 delay/clock；`ReloadSettingsAsync` / `UpdateHighlightRulesAsync` / `SetUnicycleEnabledAsync` 热更新；`DisposeAsync` 断连 + flush。

### Data

- `HighlightEvents` 表 + `IX_HighlightEvents_TriggerTime` 索引；`HighlightEventRepository`（AddAsync/GetRecentAsync/ClearAsync，UTC "O" 往返）。

### Desktop（集成）

- `Controls/DanmakuOverlay`：Avalonia 自绘，30fps 单 DispatcherTimer；车道渲染、同屏上限 30（紧凑 15）、pending 600、超限丢渲染不丢统计、阴影/礼物配色。
- `MpvPlayerView`：Video 之上、状态层之下插入 `DanmakuLayer`；`DanmakuMasterEnabled`（全局总闸）与 `DataContext` 上的 `IsDanmakuVisible`（窗格开关）共同决定可见性；`PushDanmaku/ClearDanmaku`；紧凑模式切 15 条上限。
- `MainWindowViewModel`：`_danmakuHandles` + `SyncDanmakuSubscriptions()` 在房间增删/换位/方案加载/重建/启动全部路径 reconcile；订阅只跟「房间在工作区」绑定；消息批量 UI 线程投递（tile 播放器 + 焦点浮层）；`InitializeDanmakuAsync` 启动回填高能事件；`ApplyDanmakuMasterSettingAsync` 应用全局总闸；`Dispose` 释放订阅并最多等 2s flush。
- 焦点浮层：`ToggleFocusAsync` 里 `new MpvPlayerView { DataContext = tile }` 修复弹幕开关/标题绑定；同房间共享连接（不额外 Acquire）。
- 高能事件面板：工作区右侧叠加 `Border`（不塞 Dock，避免污染 `DockLayoutJson`）；顶栏「高能事件」开关；内存 200 条 + 清空/收起 + 空态。
- 状态栏：`UnicycleBlockedText` 接 manager 全局累计拦截数。
- 设置页：屏蔽词条新增「正则」勾选；新增高能参数卡片（短窗/基线/倍数/绝对下限/冷却/加权关键词，保存/恢复默认）；`MainWindow.OnSettings` 关闭后调用 `Danmaku.ReloadSettingsAsync()` + 重新应用全局总闸。

## 与 `limoclip_v0_2.md` 的关键偏差（以本文件为准）

1. **工厂形态**：没有实现 `IPlatformProvider.CreateDanmakuClient`；改为 Core `IDanmakuClientFactory` + `Platforms/DanmakuClientFactory`，保证 `LiveMonitor.Danmaku` 只依赖 Core。
2. **B站只收不发**：V0.2 不做发送弹幕；游客可连接，登录后独轮车单用户识别才完整（游客 uid=0 按设计放行）。Cookie 白名单没有 `buvid3`，缺失时用无名 `spi` 接口兜底，失败则降级空串。
3. **弹幕连接生命周期**：只与「房间是否在工作区」绑定；单窗格弹幕开关与全局总闸（设置页 `DanmakuPreviewEnabled`）**只停渲染**，连接、高能统计、独轮车计数继续跑。
4. **正则过滤开放**：`DanmakuBlockKeywords.IsRegex` 已可用；设置页勾选即生效。
5. **高能参数持久化 + 事件落盘**：参数写 `AppSettings`；`HighlightEvent` 写 `HighlightEvents` 表，启动回填面板最近 200 条。
6. **面板落点**：右侧叠加层，不写 Dock 结构。
7. **房间号**：弹幕客户端直接用 DB 中 `LiveRoom.RoomId`（B站为 `get_info` 返回的真实 room_id），不重走短号解析。

## Step 6 真实网络验证（2026-11-22，自动化部分已完成）

- **GUI 启动冒烟**：重编后用 `cmd /c start` 直启（`LIVEMONITOR_DB` 指向 `.cache`），窗口标题 `LiveMonitor`、进程 Responding、SQLite 建库成功、`startup-error.log` 无新增 → 启动/DI/DanmakuManager 初始化正常。GUI 输出目录里的旧 `startup-error.log`（15:29）是 V0.1 历史残留，勿误判。
- **B站握手根因**：`getDanmuInfo?id=...&type=0` 原先只带 UA/Referer 时返回 `code=-352`；补 buvid Cookie、Origin、Sec-Fetch 等仍 `-352`。根因是 B站已对 `getDanmuInfo` 加 **WBI 签名**要求。用匿名 `https://api.bilibili.com/x/web-interface/nav`（`code=-101` 但 `data.wbi_img` 仍返回）取 img/sub key，按置换表算 mixin，对查询参数（`id`、`type=0`、`web_location=444.8`、`wts`）排序 + MD5 得 `w_rid` 后返回 `code=0`（token 252 字节、6 个 host）。已落地 `BilibiliWbiSigner` + 客户端密钥缓存 + 新增 5 个 signer 测试与 2 个客户端签名测试（Platforms 107→114）。
- **真实收弹幕（游客态）**：E2E 临时工程（`.cache/e2e-danmaku`，未入 sln）用真实客户端连接：B站房间 `21452505` 30s 收到 **24 条**（含 brotli 嵌套解包）；Twitch `ludwig` 30s 收到 **3 条** PRIVMSG（`eslcs` 虽然 live 但当时 chat 静默，0 条属正常）。另用原始 WS 探针验证过 B站 8 帧解析 9 条、Twitch ludwig 25s 2 条。
- 频道是否在播的探测方法：GET `https://www.twitch.tv/{channel}` HTML 匹配 `"isLiveBroadcast":true`（decapi.me 在本机 DNS 不可用）。
- 探针经验：对 `ClientWebSocket.ReceiveAsync` 用短超时 CTS 反复取消会 **abort 连接**；探针应使用单次 deadline CTS。

## 已知限制 / 待验收

1. **Step 6 剩余项（人工）**：6~9 路高热度直播间弹幕海下的 CPU/内存/UI 帧率、独轮车误杀率、高能阈值稳定性仍需人工挂机；阈值默认值可能有误报/漏报，需要拿真实直播调。B站游客态真实收发已验证通过（见上节）；登录态（SESSDATA）下 uid 非 0 与独轮车单用户识别待验证；Twitch 真实 wss 匿名收流已验证。
2. Twitch 匿名每连接 JOIN 限速与 20~30 路 per-room WS 压力未测；Twitch 侧不做独轮车判定（符合设计）。
3. B站 WBI 密钥来自匿名 `nav` 接口（缓存 1h）；若该接口后续收敛匿名访问，会抛 `InvalidOperationException`（消息含 "WBI"）并按退避重连，需要再评估备选密钥获取方式。
3. 高能落盘 `DisposeAsync` flush 无超时（仓储异常会被吞并记日志，但极端挂起时退出最多被 VM 的 2s 等待兜住）。
4. 弹幕渲染被全局关闭时仍保持连接；移出工作区才释放。
5. `_wiredTiles` 只增不减等 V0.1 既有问题未修（不属 V0.2 范围）。

## 构建 / 测试

便携 SDK 在 `.dotnet`，每次先设置：

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

```powershell
& "$root\.dotnet\dotnet.exe" build "$root\LiveMonitor.sln" --no-restore --nologo -v minimal
& "$root\.dotnet\dotnet.exe" test "$root\tests\LiveMonitor.Data.Tests\LiveMonitor.Data.Tests.csproj" --no-build --nologo -v minimal
& "$root\.dotnet\dotnet.exe" test "$root\tests\LiveMonitor.Platforms.Tests\LiveMonitor.Platforms.Tests.csproj" --no-build --nologo -v minimal
& "$root\.dotnet\dotnet.exe" test "$root\tests\LiveMonitor.Danmaku.Tests\LiveMonitor.Danmaku.Tests.csproj" --no-build --nologo -v minimal
& "$root\.dotnet\dotnet.exe" test "$root\tests\LiveMonitor.Desktop.Tests\LiveMonitor.Desktop.Tests.csproj" --no-build --nologo -v minimal
```

运行 GUI：`& "$root\.dotnet\dotnet.exe" run --project src/LiveMonitor.Desktop`（直接 Start-Process，不要把输出重定向到文件）。

## 后续建议顺序

1. 人工 Step 6 剩余部分：添加 B站热门房 + Twitch 房间，开 6~9 路弹幕与全局总闸，对比 `STEP8_REPORT.md` 记录 CPU/内存/UI 帧率；同时观察状态栏独轮车计数与高能面板。
2. 用真实直播回调高能参数（短窗 10s / 基线 180s / 3.0× / 下限 15 / 冷却 60），必要时改默认值并补文档。
3. 验证 B站登录态（SESSDATA）下 uid 非 0、独轮车规则生效；确认游客态提示。
4. 提交建议按层拆分：Core+Danmaku 契约/逻辑、Platforms 弹幕客户端、Data 落盘、Desktop 集成、docs。

## 约束（沿用 V0.1，仍有效）

- 平台逻辑保持现状：B站原生 API、Twitch `yt-dlp -j`；不换 streamlink、不处理 Twitch SSAI 广告。
- 焦点放大、音频逻辑、`PlatformCredentials`、`PlatformType` 语义保持现状；不为性能回退 `aid=no`。
- 不重构 V0.1 既有模块；界面沿用现有风格。
- GUI 用 `Start-Process` 直启、不重定向输出；截图/临时 DB/测试产物放 `.cache/`，不提交。
- `dotnet test` 的 `VSTEST_ARTIFACTS_DIRECTORY` 提示是旧 Test.Sdk 噪音，不影响结果。

## 新 session 首条消息（可直接复制）

> 你在 `E:\code\liveSurveilance`。先读 `HANDOFF.md`、`limoclip_v0_2.md`、`code_artifact.md`、`STEP8_REPORT.md`，不要改代码。复述当前 V0.2 实现状态、build/test 结果与未提交改动范围；随后按 HANDOFF 的「后续建议顺序」做 Step 6 真实直播联调与性能记录，不重构、不改平台/焦点/音频语义。
