# LiveMonitor V0.1（多屏监控室原型）— 设计评审与开发任务书（v1.7 交付版，2026-11-22）

> **配套视觉与交互基准**：本文档配合交互原型 `LiveMonitor_V0.1_Prototype.html` 一同交付，所有界面布局、操作流与状态提示均以该原型为准。

---

## 1. V0.1 范围边界与验收目标

### 1.1 明确边界（防范围蔓延）

* **本期包含（In-Scope）**：
  * 9 个项目骨架（`Core` / `Data` / `Platforms.Common` / `Platforms.Bilibili` / `Platforms.Twitch` / `Platforms` / `Desktop`，另含 Data.Tests / Platforms.Tests / Desktop.Tests）与 SQLite 持久化（WAL 模式）。平台按独立类库拆分，替换单个平台 DLL 后重启生效，运行时热重载暂未实现。
  * 直播间主库管理（增删改查、URL 自动识别平台与房间号、统一采用 **`#编号 主播名`** 格式展示，如 `#1 晚风游戏室`）。
  * 窗口方案管理（保存 / 另存为 / 切换 / 未保存提醒 / 差量 Diff 零黑屏复用）。
  * B站（HTTP-FLV + 防盗链）与 Twitch（**`yt-dlp -j` 解析 HLS**）双平台接入；Twitch 主播名由用户在添加/编辑时填写，`yt-dlp` 只负责流与标题，不保证去广告。
  * **内置 `WebView2` 平台账号登录弹窗**：自动捕获并经 Windows DPAPI 加密存储登录 Cookie/Token（解锁原画画质与会员免广告，含高并发风控分级与游客态降级）。
  * **本地统一弹幕屏蔽与独轮车开关配置**：完成设置页 UI（含关键词词库与「自动屏蔽独轮车」开关）及 SQLite 持久化；在单路分屏底部控制条预留「弹幕开/关」按钮位（默认关闭，V0.2 接入实时弹幕流后直接生效）。
  * `Dock.Avalonia` **平铺式自由分屏**：按当前直播间数量使用固定行分布（3 路 `[1,2]` 一大两小，5 路 `[3,2]` 等）自适应铺满，支持拖拽分屏边界分割线自由调整任意窗口大小，提供 `[⊞ 一键自适应排布]` 归位按钮。
  * 基于 `LibMpv.Avalonia` 的低开销播放器封装、窗口高度自适应画质、全局丢帧巡检及未开播轮询 / 断流退避重连。
* **本期明确不做（Out-of-Scope，按路线图顺延）**：
  * V0.2：B站/Twitch 实时弹幕 WebSocket 接入、独轮车过滤管线运行、高能密度检测空跑。
  * V0.3：FFmpeg 环形缓冲/全程录制、伴随弹幕落盘、本地到 NAS/云盘异步归档。
  * V0.4：弹幕密度波形剪辑台、有/无弹幕双版导出、高能片段 ASR、独立插件 DLL 热更新及抖音/斗鱼/虎牙/YouTube 补齐。

### 1.2 验收标准（双档压测）

1. **常规业务验收（6~9 路）**：
   * 建两个 **PaneId 集合一致**的方案（如方案一 `#1 #2 #3`、方案二 `#2 #3 #6`，均为 3 路 `1+2`），反复切换 10 次：相交主播 `#2 #3` 的播放器原地复用、画面零黑屏；`#1` 换 `#6` 的窗格只重启该路；已关闭主播的内存与网络句柄正常回落。若两方案的 PaneId 集合不同，则允许重建并接受一次短暂黑屏，不纳入“零黑屏”验收。
   * 连续挂机 2 小时，任务管理器中进程内存曲线平稳，无明显泄漏。
   * 未开播直播间自动显示离线占位并按 60 秒轮询；手动断网恢复后，播放中的分屏能自动重连。
2. **极限性能摸底（20~30 路）**：
   * 在 `Compact15Fps`（`vf=fps=15`）+ 音频常开但静音（`aid=auto` + `mute=yes`）+ `demuxer-max-bytes=4M`（高清 8M / 低清 3M）模式下同时拉起 20~30 路直播（由自适应算法动态铺满工作区），记录任务管理器中 **CPU、内存、GPU Video Decode、GPU Copy、GPU 3D** 五项曲线，测出本机硬件解码、显存回拷与 UI 帧率的真实拐点（当前 ANGLE/OpenGL 下 `gpu-hwdec-interop=auto` 实际回落为 `d3d11va-copy`）。20~30 路若音频解码成为瓶颈，再评估退回 `aid=no`。

---

## 2. 编码前设计评审修正项（必读防坑指南）

在敲代码前，必须严格落实以下 **6 项工程修正**：

| 序号 | 原设计隐患 | 强制修正方案 |
| :--- | :--- | :--- |
| **1** | **CLI 解析风暴**：一次切入 20 路 Twitch 直播时，`Task.WhenAll` 同时拉起 20 个 `yt-dlp` 进程会瞬间打满 CPU 并触发平台 IP 限流。 | 在 `WorkspacePlayerManager` 中引入 `SemaphoreSlim(4, 4)` 限制并发解析数上限为 4，并优先解析焦点窗口，其余窗口按队列分批起播。 |
| **2** | **Twitch 服务端广告污染**：`yt-dlp` 拉 Twitch 流会夹带 SSAI 硬拼接广告，监控室可能突然播出广告。 | V0.1 明确接受该限制（不引入 streamlink）：保持 `yt-dlp -j` 解析，Twitch 广告不做剔除；主播名由用户填写，避免依赖 yt-dlp 元数据。后续如要彻底去广告，再评估 `streamlink --twitch-disable-ads` 或自定义 M3U8 代理，不在本版范围。 |
| **3** | **Avalonia 视觉父级冲突**：把带 C++ 句柄的 `MpvPlayerView` 从旧 `ContentControl` 搬到新 `Dock` 窗格，会触发 `already has a visual parent`，且跨结构迁移 OpenGL 控件可能让 mpv 渲染初始化失败。 | **不做跨窗格搬运**。仅当新旧布局的 `PaneId` 集合一致时复用原窗格与原播放器；结构变化时销毁旧 `MpvPlayerView`，按新结构重建窗格与播放器。`PlayerView` 的唯一宿主是 `RoomTileViewModel`，`WorkspacePlayerManager` 只按 `PaneId` 管理实例。 |
| **4** | **`Dispose` 误触发**：将 `Dispose()` 写在 `OnDetachedFromVisualTree` 会导致拖拽换位或 Diff 切换时视频被直接销毁。 | 移除 `OnDetachedFromVisualTree` 中的销毁逻辑；`MpvPlayerView` 的销毁权**唯一归属** `WorkspacePlayerManager`（仅在关闭分屏、方案差集移除或主窗口关闭时触发）。 |
| **5** | **数据库重复入库**：同一主播链接被重复添加会导致主库脏数据与实例池键冲突。 | `LiveRooms` 表强制增加 `UNIQUE(Platform, RoomId)` 联合唯一索引，添加前先校验拦截。 |
| **6** | **未开播与断流空屏**：房间未开播或中途断流时没有状态流转，界面表现为永久黑屏。 | `RoomTileVM` 维护状态机：`Loading` / `Playing` / `Offline` / `Error`，离线态显示占位并启动 60 秒轮询，断流态执行 3 次指数退避重连，失败后 60 秒复检。 |

---

## 3. 数据库设计（SQLite Schema）

文件位置：`%LocalAppData%/LiveMonitor/livemonitor.db`，所有连接强制开启 `Foreign Keys=True` 与 `WAL` 模式。

```sql
PRAGMA journal_mode = WAL;

-- 1. 直播间主库（全局唯一）
CREATE TABLE IF NOT EXISTS LiveRooms (
    Id TEXT PRIMARY KEY,
    DisplayNumber INTEGER NOT NULL UNIQUE,      -- 全局递增编号，与 Alias 拼接展示为 "#1 晚风游戏室"
    Platform INTEGER NOT NULL,                  -- 1=Bilibili, 2=Douyin, 3=Douyu, 4=Huya, 5=Twitch, 6=YouTube
    RoomId TEXT NOT NULL,
    RawUrl TEXT NOT NULL,
    Alias TEXT NOT NULL,
    GroupTag TEXT,
    CreatedAt TEXT NOT NULL,
    UNIQUE(Platform, RoomId)                    -- 强制防重复入库
);

-- 2. 窗口方案主表
CREATE TABLE IF NOT EXISTS LayoutProfiles (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    IsCustomLayout INTEGER NOT NULL DEFAULT 0,  -- 0=按数量自动适配，1=用户已手动拖拽过自定义比例
    DockLayoutJson TEXT                         -- Dock.Avalonia 序列化后的精确窗口树与各窗格 Proportion 比例
);

-- 3. 窗口方案槽位关联表（多对多）
CREATE TABLE IF NOT EXISTS LayoutSlots (
    Id TEXT PRIMARY KEY,
    LayoutProfileId TEXT NOT NULL,
    LiveRoomId TEXT NOT NULL,
    PaneId TEXT NOT NULL,                       -- 绑定 DockLayoutJson 中对应的 Document Id
    DefaultMuted INTEGER NOT NULL DEFAULT 1,
    DefaultDanmakuVisible INTEGER NOT NULL DEFAULT 0, -- 多屏默认关闭画面弹幕渲染
    IsFocusSlot INTEGER NOT NULL DEFAULT 0,
    PreferredQualityId TEXT,
    FOREIGN KEY (LayoutProfileId) REFERENCES LayoutProfiles(Id) ON DELETE CASCADE,
    FOREIGN KEY (LiveRoomId) REFERENCES LiveRooms(Id) ON DELETE CASCADE,
    UNIQUE(LayoutProfileId, LiveRoomId)         -- 同一方案内禁止放入重复直播间
);

-- 4. 平台账号加密凭证表（Windows DPAPI ProtectedData 加密存储）
CREATE TABLE IF NOT EXISTS PlatformCredentials (
    Platform INTEGER PRIMARY KEY,
    AccountName TEXT,
    EncryptedCookie TEXT NOT NULL,              -- DPAPI 密文的 Base64 字符串
    UpdatedAt TEXT NOT NULL,
    IsExpired INTEGER NOT NULL DEFAULT 0
);

-- 5. 本地统一弹幕屏蔽词库（V0.1 建表与配置，V0.2 接入弹幕流后生效）
CREATE TABLE IF NOT EXISTS DanmakuBlockKeywords (
    Id TEXT PRIMARY KEY,
    Keyword TEXT NOT NULL UNIQUE,
    IsRegex INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL
);

-- 6. 全局键值配置表（存储自动屏蔽独轮车开关、弹幕预览开关等）
CREATE TABLE IF NOT EXISTS AppSettings (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_LayoutSlots_ProfileId ON LayoutSlots(LayoutProfileId);
```

---

## 4. 项目结构与依赖关系（按平台拆分类库）

```text
LiveMonitor.sln
├── src/
│   ├── LiveMonitor.Core/                 # 零外部依赖：实体、枚举、仓储与服务接口
│   ├── LiveMonitor.Data/                 # 依赖 Core：LiveMonitorDatabase + Repositories
│   ├── LiveMonitor.Platforms.Common/     # PlatformDefaults / JsonRead / YtDlpRunner
│   ├── LiveMonitor.Platforms.Bilibili/   # BilibiliProvider
│   ├── LiveMonitor.Platforms.Twitch/     # TwitchProvider（yt-dlp -j）
│   ├── LiveMonitor.Platforms/            # PlatformFactory，引用 Common/Bilibili/Twitch
│   └── LiveMonitor.Desktop/              # Avalonia UI + Dock.Avalonia + LibMpv.Avalonia + WebView2
├── tests/                                # Data.Tests / Platforms.Tests / Desktop.Tests
└── tools/                                # 构建时自动拷贝至输出目录（保留独立更新能力）
    ├── libmpv-2.dll                      # Windows x64
    ├── yt-dlp.exe                        # Twitch（及后续 YouTube）解析
    └── cacert.crt                        # mpv/libcurl 公开 CA 根证书清单
```

> 平台按独立类库拆分：替换单个平台 DLL 后重启生效，运行时热重载（AssemblyLoadContext）暂未实现。`PlatformFactory` 是唯一组合入口。

---

## 5. 核心模块实现规范

### 5.1 `MpvPlayerView`（低开销核心）
* **强制初始化参数**：
  * `hwdec=auto-safe`、`vo=libmpv`、`gpu-hwdec-interop=auto`（本机 ANGLE/OpenGL 下 d3d11 interop 不可用，硬解为 `d3d11va-copy`）
  * `cache=yes`、`demuxer-max-bytes=4M`（720P 以上 8M、低清 3M）、`demuxer-max-back-bytes=0`、`network-timeout=10`
  * 音频解码常开 `aid=auto`，默认 `mute=yes`；开声只切 `mute`，不再重建 `aid`，避免“要点一次 fps 才出声”
  * 音量表按需挂载：仅在该路开声/可听时把 `astats` 挂到 `af`，静音立即摘除；音频解码本身不关闭，保留开声语义
  * `audio-buffer=0.8`、`audio-stream-silence=yes`、`audio-exclusive=no`（多路同时开声的稳态）
* **防盗链头注入**：`User-Agent` 走 `user-agent` 属性，`Referer` 走 `referrer` 属性，其余头（如 `Cookie`）再走 `http-header-fields`，避免 UA 中的逗号截断请求头。
* **节能模式**：`SetCompactMode(true)` 注入 `vf=fps=15`，用于 $\ge 12$ 路监控或丢帧降级。
* **后台安全释放**：`Dispose()` 内用 `Interlocked.Exchange` 保证幂等，先置空 `MpvVideoView.MpvContext`，再通过 `Task.Run` 在后台线程调用 `stop` 与 `Dispose()`，防止断网销毁卡死 UI 线程。

### 5.2 `WorkspacePlayerManager`（方案切换与播放器复用）
* 维护 `Dictionary<string, MpvPlayerView> _playerByPane`，键是 **`PaneId`**，不是 `LiveRoomId`。
* 是否复用只看 **`PaneId` 集合**：新旧布局 `HasSamePaneIds` 为真时，保留原 Dock 树、原 `RoomTileViewModel` 与原 `MpvPlayerView`，只替换窗格绑定的房间数据；同一 `PaneId` 内房间未变则完全不动，房间变了才停该路并重启。
* `PaneId` 集合不一致时，`LoadProfileAsync` 先 `DisposePlayers(previousLayout)`，再按新 `DockLayoutJson` 或 Auto-Fit 结构重建窗格与播放器，最后一次性替换根布局；**不跨窗格搬运 `MpvContext`/OpenGL 控件**。
* 外部平台解析通过 `SemaphoreSlim(4, 4)` 限流；播放器起播不占该信号量。只有 `SaveAsync` 写方案时走 SQLite 事务，切换布局本身没有全局 `SwitchProfileAsync` 锁。

### 5.3 自适应画质与丢帧巡检
* **高度自适应**：监听每个分屏 `Bounds.Height` 变化（500ms 防抖）：
  * `> 720px`：拉原画（焦点/大窗）
  * `360px ~ 720px`：拉 720P
  * `< 360px`：拉保底清晰度（国内最低 720P/480P、海外最低 480P）并自动开启 `vf=fps=15`
* **丢帧巡检**：2 秒 `PeriodicTimer` 读取所有活跃实例的 `decoder-frame-drop-count + frame-drop-count` 增量；连续 3 周期整体丢帧率 $> 5\%$ 时，自动将非焦点分屏切入 `15fps` 并在顶部状态栏亮黄灯提示。

---

## 6. 交互约定、Dock 绑定规范与异常边界

### 6.1 主界面布局与核心操作流（以 `LiveMonitor_V0.1_Prototype.html` 为准）

* **顶部工具栏（48px）**：侧边栏折叠按钮、方案下拉切换器、未保存黄点提示、`[保存]` / `[另存为]`、`[⊞ 一键自适应排布]` 按钮与当前排版模式标签（`自动适配` / `自定义比例`）、解码健康度指示灯、`[账号与设置]` 按钮。
* **左侧直播间主库（260px，可折叠）**：
  * 列表项统一按 **`#编号 主播名`** 格式展示（例如 `#1 晚风游戏室`、`#2 电竞阿泽`、`#6 ShroudTV`），附带平台徽章、分组标签、在线绿点/离线灰点及“监控中”状态标记。
  * 点击 `[+ 添加]` 弹出模态框，粘贴链接自动识别平台与 `RoomId`，自动分配递增的 `DisplayNumber`；主播名称留空则以 `平台_房间号` 补齐；若 `(Platform, RoomId)` 已存在则拦截提示。
  * **双击**列表项加入右侧工作区；若该直播间已在当前方案中打开，则高亮闪烁定位到已有分屏，禁止重复打开。
* **分屏底部悬浮控制条**：鼠标移入显示，包含**静音/开声、画质下拉、15fps 节能、弹幕开/关（默认关，预留给 V0.2）、焦点放大、关闭**。
* **平铺式自适应与自由调大小规则**：
  * **采用平铺式（Tiling），不做重叠自由窗**：基于 `Dock.Avalonia` 的 `ProportionalDock` 与 `ProportionalDockSplitter` 实现，窗口之间严丝合缝，拖动分割线拉大某窗格时自动挤压相邻窗格（并联动触发 `Bounds.Height` 自适应升降画质）。
  * **按数量自动适应算法（Auto-Fit）**：在 `IsCustomLayout == false` 时使用固定行分布（最多 30 路）：1=[1]、2=[2]、3=[1,2]（上 1 下 2 一大两小）、4=[2,2]、5=[3,2]、6=[3,3]、7=[3,2,2]、8=[3,3,2]、9=[3,3,3]、10=[4,3,3]、11=[4,4,3]、12=[4,4,4]；13+ 按 $\text{cols} = \lceil \sqrt{N \cdot \frac{W}{H} \cdot \frac{9}{16}} \rceil$ 均分。行列数不变时只重置比例、不重建播放器；有空格必须先重建，避免空窗格占位。
  * **防状态冲突保护**：一旦用户手动拖拽分割线调整了任意窗口大小或自定义了停靠位置，当前方案自动切换为 `自定义比例`（`IsCustomLayout = true`）并标脏——此后增删直播间仅在末尾局部追加/移除，**绝不强行重排毁掉用户调好的主副屏比例**；点击顶部 `[⊞ 一键自适应排布]` 可主动重置回整齐自适应网格。
* **方案未保存拦截**：增删分屏、拖拽换位、拖拽分割线调大小、切换画质或静音均标记脏状态（黄点）；切换其他方案时弹窗提示：**`[保存并切换]` / `[放弃修改]` / `[取消]`**。
* **焦点窗口（最大化）**：**双击**任意视频分屏或点右上 `⛶` 进入独立全屏浮层；浮层用单独 `MpvPlayerView` 拉最高档并开声，原窗格全部静音；再次双击浮层、按 Esc 或点退出按钮关闭浮层，还原原平铺比例、静音状态与画质。已知代价：浮层会额外建立一路播放连接。底部控制条另有弹幕开/关占位（V0.1 默认关，V0.2 接实时弹幕）。

### 6.2 `Dock.Avalonia` 反序列化与 `MpvPlayerView` 生命周期（同 PaneId 复用 / 跨结构重建）

`Dock.Serializer` 只能序列化纯数据 `RoomTileViewModel`，不能序列化持有 C++ 句柄的 `MpvPlayerView`；播放器由 `WorkspacePlayerManager` 按 `PaneId` 独占管理。加载/切换方案按以下两条路径处理：

1. **反序列化与初始化**：若 `DockLayoutJson` 非空，用 `Dock.Serializer` 还原 `IRootDock`；否则按 `Slots` 数量调用 Auto-Fit 生成 `ProportionalDock` 与 `ProportionalDockSplitter`。加载旧 JSON 后执行 `EnsureSplitters` 与 `EnableDragging`，再 `InitLayout`。
2. **绑定窗格数据**：`BindTiles` 按 `PaneId` 从 `LayoutProfile.Slots` 找房间，构造/更新 `RoomTileViewModel`；`PlayerView` 仍由 `WorkspacePlayerManager` 持有，不写进 JSON。
3. **同 PaneId 集合**：若 `previousLayout is not null && HasSamePaneIds(previousLayout, layout)`，**不替换 Dock 树**。逐个 `PaneId` 对比房间：房间未变则保留现有 `MpvPlayerView`；房间变了先 `Stop`/`Dispose` 旧播放器，再 `Assign` 新房间并重启该路。此路径不会跨容器搬运控件。
4. **跨结构**：`PaneId` 集合不同（增删窗格、行列变化、不同 Auto-Fit 行分布等），无法安全迁移 OpenGL 控件。先 `DisposePlayers(previousLayout)`，再按新树构造全新 `RoomTileViewModel` 与播放器，最后一次性把新根节点赋给 `DockControl.Layout`；重启当前在播房间，接受一次短暂黑屏。
5. **收尾**：同步 `GridRows`/`GridColumns`、`IsCustomLayout`、`IsDirty` 与状态文本；只有同 PaneId 路径才允许宣称“播放器原地保留/零黑屏”。

### 6.3 平台账号绑定（`WebView2`）、高并发风控与广告策略

* **内置登录捕获**：在「账号与设置」弹窗中为 B站 与 Twitch 提供 `[内置窗口登录]` 按钮。点击后弹出独立 `WebView2` 窗口加载官方登录页，登录完成后通过 `CoreWebView2.CookieManager` 提取关键凭证（如 B站 `SESSDATA` / `bili_jct`、Twitch `auth-token`），使用 Windows **DPAPI (`ProtectedData.Protect`)** 加密写入 `PlatformCredentials` 表，随即销毁 `WebView2` 释放内存。
* **高并发单账号防风控分级**：
  * 仅在 **焦点原画窗口** 或 **同平台活跃路数 $\le 4$** 时携带已绑定账号的 Cookie 拉流。
  * 在 **20~30 路高并发监控** 场景下，非焦点低画质小窗默认走**游客态拉流**，防止单账号高频并发触发平台限流或封禁。
* **Twitch 流解析**：`TwitchProvider` 通过 `CliWrap` 调用 `yt-dlp -j --no-playlist --no-warnings --socket-timeout 15 --retries 2 https://www.twitch.tv/{roomId}`，解析 `formats` 并按高度/帧率去重；V0.1 不剔除 Twitch 广告，也不再提供 M3U8 代理设置。主播名由用户填写（添加/编辑直播间时的名称/别名），不依赖 yt-dlp 元数据。
* **游客态降级与过期提示**：未绑定或 Cookie 失效时，自动回退游客态可获取的最高画质并在该分屏 HUD 标注「游客画质」或提示重新登录，不阻断播放。

### 6.4 本地统一弹幕屏蔽与独轮车过滤规范

* **设计原则**：客户端直连各平台原始弹幕通道，不依赖也不尝试同步各平台私有屏蔽词；统一由本地维护跨平台词库。
* **V0.1 落地范围**：完成设置弹窗内的「本地弹幕屏蔽」Tab，支持关键词增删改查、勾选「监控画面弹幕预览」与「**自动屏蔽独轮车**」开关，并持久化至 `DanmakuBlockKeywords` 与 `AppSettings` 表。
* **V0.2 接入实时弹幕流后的管线规则**：
  * **独轮车 3 条判定规则**：① 单用户 60 秒内发送 $>3$ 条清洗后相似度 $>80\%$ 弹幕，或连续 4 条发言间隔标准差 $<0.5$ 秒（临时静默 10 分钟）；② 单用户 30 秒内连发 $\ge 3$ 条长度 $>15$ 字的长文本（小说/文案切片）；③ 全局 15 秒内 $>3$ 个不同账号发送完全一致的长句（长度 $>10$ 字）。
  * **防误伤硬边界**：长度 $\le 6$ 字的短情绪词（如 `666`、`？？？`、`哈哈哈`、`神回`）**豁免跨账号复读拦截**；且过滤管线必须前置在 `IHighlightDetector` 与弹幕落盘之前执行。

### 6.5 未开播轮询与断流重连策略

* **未开播轮询（`Offline` 态）**：后台维护单个轻量级 `PeriodicTimer`（默认每 **60 秒** 巡检一次当前方案中处于 `Offline` 态的直播间），检测已开播后自动拉流切换至 `Playing` 态。
* **断流自动重连**：监听 `libmpv` 的 `end-file` / 网络错误事件，执行 **3 次指数退避重连**（间隔 3 秒 → 10 秒 → 30 秒，重新向 `IPlatformProvider` 获取新流地址防止原 URL Token 过期）；3 次均失败后进入失败态并每 **60 秒**复检一次，网络恢复后自动重启该路，同时保留手动 `[重连]` 按钮。

---

## 7. V0.1 8 步开发执行清单

- [x] **Step 1：初始化解决方案与外部依赖**
  * 创建 `Core` / `Data` / `Platforms.Common` / `Platforms.Bilibili` / `Platforms.Twitch` / `Platforms` / `Desktop` 与三个测试项目，安装 `Microsoft.Data.Sqlite`、`CliWrap`、`CommunityToolkit.Mvvm`、`Semi.Avalonia`、`Dock.Avalonia`、`LibMpv.Avalonia`、`Microsoft.Web.WebView2`，放置 `libmpv-2.dll`、`yt-dlp.exe`、`cacert.crt`。
- [x] **Step 2：编写 Core 层实体与接口**
  * 已落地 `LiveRoom`（含 `DisplayNumber`）、`LayoutProfile`（含 `IsCustomLayout`）、`LayoutSlot`、`PlatformCredential`、`DanmakuBlockKeyword`、`AppSetting` 及仓储、平台与播放器接口。
- [x] **Step 3：落地 Data 层 SQLite 初始化与仓储**
  * 已实现 `LiveMonitorDatabase`（外键、WAL、6 张表与索引）、`RoomRepository`、`LayoutProfileRepository`（事务保存、同方案房间唯一）、`CredentialStore`（DPAPI 加解密）与其余仓储；9 个 Data 测试通过。
- [x] **Step 4：打通 B站 与 Twitch 流地址解析**
  * 已实现 `BilibiliProvider`（可选 Cookie + Referer/UA 防盗链）、`TwitchProvider`（`CliWrap` 调 `yt-dlp -j`，解析 formats，主播名由用户填写）及 `PlatformFactory` URL 自动识别；Platforms 38 个测试通过。
- [x] **Step 5：封装低开销 `MpvPlayerView` 控件**
  * 已注入低内存、音频常开 + 静音、防盗链与 `audio-buffer` 参数；实现 `GetHealthMetrics()`、`SetCompactMode()`、断流监听、后台线程安全 `Dispose()`；底部弹幕开关位仍为 V0.1 占位。**不绑定视觉树自动销毁**。
- [x] **Step 6：搭建主库边栏、WebView2 登录与设置弹窗、Dock 工作区**
  * 已实现左侧主库（`#编号 主播名`、搜索、分组 pills）、设置弹窗（WebView2 登录捕获 Cookie、屏蔽词、独轮车开关）、右侧 Dock 平铺工作区（固定行分布自适应、分割线拖拽、手动换位、`[⊞ 一键自适应排布]` 与 `[↶ 恢复比例]`）。
- [x] **Step 7：实现方案持久化、同 PaneId 原地复用/跨结构重建与未开播轮询**
  * 已落地方案保存/另存/切换、未保存拦截、同 PaneId 结构的播放器原地复用、跨结构重建、60 秒离线轮询与 3/10/30 秒退避重连；`SemaphoreSlim(4)` 已包住平台状态解析（B站 HTTP / yt-dlp 进程），播放器起播不占该信号量。
- [~] **Step 8：接入自适应画质、丢帧巡检与双档压测验收**
  * 已落地 `Bounds.Height` 防抖切画质、2 秒丢帧巡检、全局健康灯与音频快照恢复；6~9 路常规验收已过，20~30 路极限性能摸底尚未完成。