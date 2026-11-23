# LimoClip V0.5（云端 ASR、热更新底座与全平台扩展）— 详细设计实施文档

> **推进策略**：V0.5 包含三条复杂度差异极大的工作线，为避免国内平台反爬逆向拖垮主线交付，强制按 **V0.5-A（剪辑台云端 ASR）→ V0.5-B（热更新底座 + YouTube）→ V0.5-C（虎牙 / 斗鱼 / 抖音独立插件）** 三个串行子阶段推进，每阶段独立可验收、可发版。

---

## 1. V0.5 范围边界与验收目标

### 1.1 明确边界（In-Scope / Out-of-Scope）

* **V0.5-A：剪辑台云端 ASR（高能片段语音转文字）**
  * 仅接云端 OpenAI 兼容 `/v1/audio/transcriptions` 接口，不做本地离线模型（不引入 `Sherpa-ONNX` / VAD）。
  * 设置页新增「语音识别（ASR）」分区（注意：`SettingsWindow` 现为分区式 `ScrollViewer`，没有 `TabControl`，直接新增 Border 分区即可），内置 **硅基流动（国内版）**、**SiliconFlow（国际版）**、**Groq** 与 **自定义兼容接口** 四套预设；API Key 复用 `DpapiCookieProtector` 经 Windows DPAPI 加密持久化。
  * 在 `ClipStudioView` 左侧高能推荐卡片及当前选区增加 **`[🎙 语音转写]`** 按钮：通过 FFmpeg 将选定区间抽轨为 `16kHz 单声道 64kbps MP3` 上传识别，兼容返回分段时间戳（`verbose_json` 点击单句跳转）与纯文本（`text`）两种响应格式。
  * 转写结果落盘至本地缓存（`%LocalAppData%/LiveMonitor/cache/asr/`），二次打开同一录像/高能区间秒级展示，不重复消耗用户 API 额度。
* **V0.5-B：三层热更新底座 + YouTube 接入**
  * 定义统一的插件入口契约 `IPlatformPlugin`，将现有的 `LiveMonitor.Platforms.Bilibili` 与 `LiveMonitor.Platforms.Twitch` 改造为可动态加载的独立插件包。
  * 落地 `PlatformPluginLoader`（基于 .NET `AssemblyLoadContext` + **Ed25519 数字签名校验**）与 `PluginUpdateService`（从 GitHub / Gitee 拉取 `providers-manifest.json` 检查并下载更新）。
  * 落地 `CliUpdater`：在设置页提供一键检查/更新 `tools/yt-dlp.exe`（支持自定义 GitHub 镜像前缀）。
  * 接入 **YouTube**（`LiveMonitor.Platforms.YouTube`）：拉流复用 `YtDlpRunner`，弹幕通过 YouTube InnerTube 协议轮询实时聊天流（无需官方 API Key），并在 `WebView2` 登录页新增 YouTube 账号绑定（解锁会员免广告与画质）。
* **V0.5-C：国内三大平台插件补齐（虎牙 / 斗鱼 / 抖音）**
  * 按复杂度由低到高，依次以独立插件项目落地 **虎牙**（`LiveMonitor.Platforms.Huya`）、**斗鱼**（`LiveMonitor.Platforms.Douyu`）与 **抖音**（`LiveMonitor.Platforms.Douyin`）。
  * 斗鱼与抖音引入纯 C# 轻量 JS 引擎 `Jint`，支持把高频变动的混淆签名脚本作为纯 `.js` 文件随 `providers-manifest.json` 热更新。
  * 扩展 `WebView2` 登录支持至虎牙、斗鱼、抖音（抖音强依赖有效 Cookie / `ttwid`）。
  * 独轮车过滤器依然**仅对 B站启用**，新增的 4 个平台弹幕仅走 `KeywordFilter` 与 `SlidingWindowDetector`。
* **本期明确不做（Out-of-Scope）**：
  * 本地离线 ASR（`Sherpa-ONNX` / `Whisper.net`）。
  * 对整场数小时全程录像执行全量 ASR（硬性禁止，仅允许对高能区间或用户框选的短片段调用，单次时长上限建议 $\le 15$ 分钟）。
  * Twitch 服务端广告绕过 / `vaft` 逆向对抗。

### 1.2 分阶段验收标准

1. **V0.5-A 验收**：
   * 分别使用硅基流动（国内直连）与 Groq（海外）测试一段 2 分钟高能片段：抽音 + 上传 + 展示全流程在 15 秒内完成；Groq 返回的分段句子点击后预览播放器精准跳转至对应时间点；重启软件再次打开该场次命中缓存，不发起网络请求。
2. **V0.5-B 验收**：
   * 篡改本地某个 `Provider.*.dll` 的任意 1 个字节后启动或触发热更新，Ed25519 验签必须拦截并拒绝加载，同时回退至内置版本或给出明确错误提示。
   * 替换新版本且签名合法的 `Provider.Bilibili.dll`，客户端能识别版本号变化并成功加载。
   * YouTube 直播间可正常在监控室播放、在录制中心直录 `.mkv` + `.jsonl` 并在剪辑台回放。
3. **V0.5-C 验收**：
   * 虎牙、斗鱼、抖音均支持粘贴链接自动识别平台与 `RoomId`、正常起播、接收实时弹幕并支持全程录制；任意单个平台接口失效时，不影响其余 5 个平台运行，且可通过更新对应插件 DLL / JS 脚本独立修复。

---

## 2. V0.5 编码前设计评审修正项（防坑指南）

| 序号 | 潜在隐患 | 强制修正方案 |
| :--- | :--- | :--- |
| **1** | **`AssemblyLoadContext` 类型隔离崩溃**：若插件项目打包时把 `LiveMonitor.Core.dll` 也复制进插件目录，`AssemblyLoadContext` 重复加载后会导致 `IPlatformPlugin` 被 CLR 视为两个不同类型，强转时直接抛出 `InvalidCastException`。 | ① 插件 `.csproj` 引用 `LiveMonitor.Core` 与 `LiveMonitor.Platforms.Common` 时必须显式标注 `<Private>false</Private><ExcludeAssets>runtime</ExcludeAssets>`；② 自定义 `PluginLoadContext.Load()` 遇到 `LiveMonitor.Core`、`LiveMonitor.Platforms.Common` 或 `System.*` 时强制返回 `null`，交由默认上下文（`AssemblyLoadContext.Default`）共享同一类型定义。 |
| **2** | **只校 SHA256 的供应链投毒风险**：若 `providers-manifest.json` 与插件 DLL 托管在同一镜像源，镜像被劫持时攻击者可以同时替换 DLL 和 SHA256，导致用户电脑执行恶意 .NET 代码。 | 客户端代码里硬编码开发者的 **Ed25519 公钥**（32 字节 Base64）；发布插件时在本地用私钥对 DLL 字节流签名写入 `manifest.json` 的 `signature` 字段；客户端下载后必须先用 `BouncyCastle.Cryptography`（NuGet 包 ID；namespace 仍为 `Org.BouncyCastle.*`；纯托管 C#，无原生依赖；实测可用版本 2.7.0；`Org.BouncyCastle.Cryptography` 这个包 ID 在 nuget.org 不存在）校验 Ed25519 签名，通过后才允许落盘和加载。 |
| **3** | **热卸载旧 DLL 内存泄漏与文件锁死**：Windows 下直接 `LoadFromAssemblyPath` 会锁定磁盘上的 `.dll` 文件，且若旧插件还有未退出的 WebSocket 或事件引用，可回收 ALC 无法立即卸载。 | ① `PluginLoadContext` 加载 DLL 时通过 `File.ReadAllBytes` 读入 `MemoryStream` 再调用 `LoadFromStream`，彻底避免磁盘文件锁死；② 检测到新版本下载验签通过后，若该平台当前有活跃播放/录制，提示“插件已就绪，停止该平台任务或重启后自动生效”；无活跃任务时直接原子切换 `PlatformFactory` 映射表。 |
| **4** | **ASR 文件体积超限与格式不兼容**：多数 OpenAI 兼容接口限制单文件 $\le 25\text{MB}$；且不同服务商返回结构不同（Groq 支持 `verbose_json` 带时间戳，硅基流动 SenseVoice 只返回纯 `{"text": "..."}`）。 | ① 抽音强制压缩为 `-vn -ac 1 -ar 16000 -c:a libmp3lame -b:a 64k`（15 分钟仅约 $7.2\text{MB}$），并在发起前校验片段时长 $\le 15$ 分钟；② 解析器优先尝试读取 `segments` 数组，缺省时回退读取顶层 `text` 字段包装为单条 `AsrTranscriptSegment`。 |
| **5** | **YouTube InnerTube 轮询被 429 限流**：无视服务端返回的轮询间隔高频请求 `get_live_chat` 会被 Google 临时封禁 IP。 | 严格读取每次响应体中 `continuationContents.liveChatContinuation.continuations[0]` 下的 `timeoutMs`（通常 1000~5000ms），做 `Math.Clamp(timeoutMs, 1000, 15000)` 等待后再发起下一次请求。 |
| **6** | **抖音高强度风控导致开箱不可用**：抖音网页端接口对 `ttwid`、`__ac_nonce` 及签名校验极严，纯无 Cookie 请求经常直接返回空流或要求滑动验证码。 | 在 UI 明确提示抖音房间“建议先通过内置 WebView2 登录/访问一次以捕获 `ttwid` 与会话 Cookie”；插件层捕获风控响应时返回清晰的 `RoomStatus` 错误提示而非静默黑屏。 |

---

## 3. V0.5-A：剪辑台云端 ASR 详细设计

### 3.1 内置服务商预设表

| 预设 ID | 显示名称 | 默认 Endpoint | 默认 Model | 返回格式支持 |
| :--- | :--- | :--- | :--- | :--- |
| `siliconflow_cn` | 硅基流动（国内推荐） | `https://api.siliconflow.cn/v1/audio/transcriptions` | `FunAudioLLM/SenseVoiceSmall` | `json`（整段文本） |
| `siliconflow_global` | SiliconFlow（海外中文） | `https://api.siliconflow.com/v1/audio/transcriptions` | `FunAudioLLM/SenseVoiceSmall` | `json`（整段文本） |
| `groq` | Groq（海外/外语推荐） | `https://api.groq.com/openai/v1/audio/transcriptions` | `whisper-large-v3-turbo` | `verbose_json`（带分段时间戳） |
| `custom` | 自定义 OpenAI 兼容接口 | 用户自填 | 用户自填 | 自动探测（优先 `verbose_json`，400 回退 `json`） |

### 3.2 Core 层契约与配置键

在 `LiveMonitor.Core` 新增：
* `AppSettingKeys`：`AsrProviderPreset`、`AsrEndpoint`、`AsrModel`、`AsrEncryptedApiKey`（经 `DpapiCookieProtector` 加密存储）、`AsrLanguage`（默认 `auto`）。
* 模型与接口：

```csharp
namespace LiveMonitor.Core.Models;

public record AsrTranscriptSegment(
    TimeSpan RelativeStart,  // 相对于本次转写片段起点的偏移
    TimeSpan RelativeEnd,
    string Text
);

public record AsrTranscriptResult(
    TimeSpan ClipStart,
    TimeSpan ClipEnd,
    string FullText,
    IReadOnlyList<AsrTranscriptSegment> Segments,
    string ProviderLabel,
    DateTimeOffset CreatedAtUtc
);
```

```csharp
namespace LiveMonitor.Core.Interfaces;

public interface IAsrProvider
{
    Task<AsrTranscriptResult> TranscribeSegmentAsync(
        string videoPath,
        TimeSpan startTime,
        TimeSpan endTime,
        CancellationToken ct = default);
}
```

### 3.3 剪辑台交互集成与缓存

1. **缓存键**（以实现为准，含 Model 且用绝对路径/UTC Ticks）：`SHA256($"{Path.GetFullPath(videoPath)}|{fileSize}|{lastWriteUtc.Ticks}|{(long)start.TotalMilliseconds}|{(long)end.TotalMilliseconds}|{model}")`，结果存为 `%LocalAppData%/LiveMonitor/cache/asr/{hash}.asr.json`。缓存文件损坏时按未命中处理并重新请求。
2. **UI 入口**：
   * 左侧每张高能推荐卡片底部增加小按钮 **`[🎙 转写]`**；点击后显示加载中，完成后在卡片下方展开文字摘要（若带分段时间戳，点击任意一句直接将 `LocalClipPlayerView` Seek 至 `ClipStart + seg.RelativeStart`）。注意：当前高能卡片 `DataTemplate` 的根元素是整体跳转用的 `Button`（[ClipStudioView.axaml:129-141](src/LiveMonitor.Desktop/Views/ClipStudioView.axaml#L129-L141)），实现时需把根改为 `Grid`/`StackPanel`，让「跳转」和「🎙 转写」是两个并列 Button，避免嵌套 Button 的 Click 冒泡冲突。
   * 右侧导出面板当前选区下方提供 **`[🎙 识别当前选区语音]`** 按钮，识别结果支持一键复制文本（方便直接当切片标题/简介）。

---

## 4. V0.5-B：热更新底座与 YouTube 接入

### 4.1 统一插件入口契约 (`IPlatformPlugin`)

在 `LiveMonitor.Core/Interfaces/IPlatformPlugin.cs` 定义统一插件契约，所有平台项目（含改造后的 B站/Twitch）均实现此接口：

```csharp
namespace LiveMonitor.Core.Interfaces;

public interface IPlatformPlugin
{
    PlatformType Platform { get; }
    string PluginId { get; }      // 如 "bilibili", "twitch", "youtube", "huya", "douyu", "douyin"
    string Version { get; }       // SemVer，如 "1.0.0"

    IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath);
    // 返回 null 表示该平台不支持弹幕客户端：DanmakuManager 用 null 表示“停止重试”，
    // 不得用抛异常代替（见 DanmakuManager.cs:490-494）。
    IDanmakuClient? CreateDanmakuClient(string roomId, string? authCookie = null, HttpClient? httpClient = null);
}
```

### 4.2 `providers-manifest.json` 托管规范（GitHub / Gitee）

仓库中维护一个清单文件（客户端内置主地址 GitHub Raw + 备用镜像 Gitee Raw，设置页支持用户覆盖镜像地址）：

```json
{
  "manifestVersion": 1,
  "updatedAt": "2026-11-23T12:00:00Z",
  "plugins": [
    {
      "pluginId": "bilibili",
      "platform": 1,
      "version": "1.0.1",
      "minAppVersion": "0.5.0",
      "dllName": "LiveMonitor.Platforms.Bilibili.dll",
      "downloadUrl": "https://github.com/heyiai02/limoclip-plugins/releases/download/v1.0.1/LiveMonitor.Platforms.Bilibili.dll",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "ed25519Signature": "<Base64 64-byte signature over raw DLL bytes>",
      "extraScripts": []
    },
    {
      "pluginId": "douyu",
      "platform": 3,
      "version": "1.0.0",
      "minAppVersion": "0.5.0",
      "dllName": "LiveMonitor.Platforms.Douyu.dll",
      "downloadUrl": "...",
      "sha256": "...",
      "ed25519Signature": "...",
      "extraScripts": [
        {
          "name": "douyu_sign.js",
          "downloadUrl": "...",
          "sha256": "...",
          "ed25519Signature": "..."
        }
      ]
    }
  ]
}
```

* 本地插件落地目录：`%LocalAppData%/LiveMonitor/plugins/{pluginId}/`。
* **加载优先级**：启动时 `PlatformPluginLoader` 优先扫描本地插件目录，验签通过且版本高于内置版本时走热加载；否则回退使用主程序输出目录自带的内置版本，保证离线可用。

### 4.3 `CliUpdater`（`yt-dlp.exe` 自动更新）

* 在设置页「组件与插件更新」分区提供 **`[检查 yt-dlp 更新]`** 与 **`[检查平台插件更新]`**。
* 调用 `yt-dlp.exe --version` 获取本地版本，对比 GitHub `yt-dlp/yt-dlp/releases/latest` 的 `tag_name`，下载至 `tools/yt-dlp.exe.tmp` 校验非空且可执行后，原子替换 `tools/yt-dlp.exe`。

### 4.4 YouTube 插件 (`LiveMonitor.Platforms.YouTube`)

* **URL 识别**：兼容 `youtube.com/watch?v={id}`、`youtube.com/live/{id}`、`youtu.be/{id}`、`youtube.com/@{handle}/live`。
* **拉流解析**：直接复用 `LiveMonitor.Platforms.Common` 中的 `YtDlpRunner` 执行 `yt-dlp -j`，筛选 `is_live == true` 的 HLS/DASH 格式并按高度/帧率去重；若绑定了 YouTube Cookie，通过临时 Netscape Cookie 文件或 `--add-header` 透传。
* **实时聊天流 (`YouTubeDanmakuClient`)**：
  1. HTTP GET 直播间页面，正则提取 `INNERTUBE_API_KEY` 与初始 `continuation` token。
  2. 循环 POST `https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={apiKey}`，解析 `addChatItemAction` 中的 `liveChatTextMessageRenderer`（普通弹幕）与 `liveChatPaidMessageRenderer`（SuperChat，标记 `IsGiftOrSuperChat = true`）。
  3. 每次响应更新下一次的 `continuation` 与 `timeoutMs`，严格按 `timeoutMs` 延时轮询。

---

## 5. V0.5-C：国内三大平台插件技术要点

| 平台插件 | 拉流解析要点 | 弹幕协议要点 | 特殊依赖与风控边界 |
| :--- | :--- | :--- | :--- |
| **虎牙** (`Platforms.Huya`) | 请求房间页提取 `hyPlayerConfig` / 移动端 API 获取 FLV/HLS 流地址，计算鉴权参数（`wsSecret` / `seqid` / `ctype`），注入对应 UA。 | 连接 `wss://cdnws.api.huya.com`，基于轻量自写 **Tars/Wup 二进制编解码器** 发送进房包与 60 秒心跳，解包 `MessageNotice`（CmdId `1400`）。 | 零额外重依赖；Tars 只需实现 Struct/Int/String/ByteArray 最小子集（约 250 行代码）。 |
| **斗鱼** (`Platforms.Douyu`) | 请求 `getH5Play` 前需执行前端下发的混淆 JS 签名函数（`ub98484234`），通过 **`Jint`** 在内存中执行并拼接 `did` 与时间戳拿到 FLV/HLS 流。 | 连接 `wss://danmuproxy.douyu.com:8501`~`8506`，按小端 12 字节头 + **STT 键值对序列化** 发送 `type@=loginreq/` 与 `type@=joingroup/rid@{id}/gid@-9999/`，45 秒心跳，解析 `type@=chatmsg/`。 | 引入 `Jint` NuGet；混淆签名包装脚本支持放在 `manifest.json` 的 `extraScripts` 里单独热更新。 |
| **抖音** (`Platforms.Douyin`) | 请求 `webcast/room/web/enter/`（携带 `WebView2` 捕获的 `ttwid` 与 Cookie），提取 `stream_url.flv_pull_url` / `hls_pull_url_map` 多档清晰度。 | 连接 `wss://webcast5-ws-web-lf.douyin.com/webcast/im/push/v2/`（需签名参数，可通过 `Jint` 加载热更新签名 JS），接收 **Protobuf** `PushFrame` → Gzip 解压 → `Response` → 回复 `Ack` → 解析 `WebcastChatMessage`。 | 风控最强；Protobuf 只需用手写轻量 Reader 或 `Google.Protobuf` 解析 4 个核心消息体；无 Cookie 时提示用户先通过内置 `WebView2` 访问登录。 |

---

## 6. 核心 C# 代码实现参考

### 6.1 OpenAI 兼容云端 ASR 客户端 (`LiveMonitor.Media/Asr/OpenAiCompatibleAsrProvider.cs`)

核心要点：FFmpeg 流式抽取 `16kHz 单声道 64kbps MP3`（挂入 `ProcessJobGuard` 并在 `finally` 清理临时音频）、自动适配 Groq `verbose_json` 与硅基流动纯 `text` 响应、支持本地磁盘缓存防重复扣费。

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Media.Asr;

public sealed record AsrApiConfig(
    string PresetId,
    string Endpoint,
    string Model,
    string ApiKey,
    string Language = "auto"
);

public sealed class OpenAiCompatibleAsrProvider : IAsrProvider
{
    private static readonly TimeSpan MaxSegmentDuration = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;
    private readonly string _ffmpegPath;
    private readonly ProcessJobGuard? _jobGuard;
    private readonly Func<Task<AsrApiConfig?>> _configAccessor;
    private readonly string _cacheDirectory;

    public OpenAiCompatibleAsrProvider(
        HttpClient httpClient,
        string ffmpegPath,
        ProcessJobGuard? jobGuard,
        Func<Task<AsrApiConfig?>> configAccessor,
        string cacheDirectory)
    {
        _httpClient = httpClient;
        _ffmpegPath = ffmpegPath;
        _jobGuard = jobGuard;
        _configAccessor = configAccessor;
        _cacheDirectory = cacheDirectory;
    }

    public async Task<AsrTranscriptResult> TranscribeSegmentAsync(
        string videoPath,
        TimeSpan startTime,
        TimeSpan endTime,
        CancellationToken ct = default)
    {
        if (endTime <= startTime)
            throw new ArgumentException("出点必须大于入点。");

        var duration = endTime - startTime;
        if (duration > MaxSegmentDuration)
            throw new InvalidOperationException($"单次语音转写片段不能超过 {(int)MaxSegmentDuration.TotalMinutes} 分钟，请缩小选区后重试。");

        var config = await _configAccessor().ConfigureAwait(false);
        if (config is null || string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("尚未配置语音识别 API，请前往「账号与设置 → 语音识别」填写 API Key。");

        // 1. 检查本地缓存
        string cachePath = BuildCachePath(videoPath, startTime, endTime, config.Model);
        if (File.Exists(cachePath))
        {
            var cachedJson = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
            var cached = JsonSerializer.Deserialize<AsrTranscriptResult>(cachedJson);
            if (cached is not null) return cached;
        }

        // 2. 用 FFmpeg 抽取 16kHz 单声道 64kbps MP3 临时文件
        string tempAudioPath = Path.Combine(Path.GetTempPath(), $"LimoClip_Asr_{Guid.NewGuid():N}.mp3");
        try
        {
            await ExtractAudioClipAsync(videoPath, tempAudioPath, startTime, duration, ct).ConfigureAwait(false);

            // 3. 发送 multipart/form-data 请求（SiliconFlow 用 json，Groq/其他优先 verbose_json）
            bool preferVerbose = !config.PresetId.StartsWith("siliconflow", StringComparison.OrdinalIgnoreCase);
            string responseBody = await SendTranscriptionRequestAsync(config, tempAudioPath, preferVerbose, ct)
                .ConfigureAwait(false);

            var result = ParseResponse(responseBody, startTime, endTime, $"{config.PresetId} ({config.Model})");

            // 4. 写入本地缓存
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(result), ct).ConfigureAwait(false);

            return result;
        }
        finally
        {
            try { if (File.Exists(tempAudioPath)) File.Delete(tempAudioPath); } catch { /* ignore */ }
        }
    }

    private async Task ExtractAudioClipAsync(string videoPath, string outputMp3, TimeSpan start, TimeSpan duration, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(start.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("64k");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outputMp3);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg 抽音。");
        // 注意：ProcessJobGuard 现网 API 是 TryAssign(Process)（返回 bool、失败只记录错误码），
        // 不是 AddProcess；照抄旧设计会编译失败。
        _jobGuard?.TryAssign(proc);

        using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
        string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0 || !File.Exists(outputMp3))
            throw new InvalidOperationException($"提取片段音频失败: {stderr}");
    }

    private async Task<string> SendTranscriptionRequestAsync(AsrApiConfig config, string audioPath, bool useVerboseJson, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(audioPath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        form.Add(fileContent, "file", "clip.mp3");
        form.Add(new StringContent(config.Model), "model");
        if (useVerboseJson)
        {
            form.Add(new StringContent("verbose_json"), "response_format");
        }
        if (!string.IsNullOrWhiteSpace(config.Language) &&
            !config.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(config.Language), "language");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, config.Endpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 若自定义接口不支持 verbose_json 返回 400，自动降级为标准 json 重试一次
        if (!resp.IsSuccessStatusCode && useVerboseJson && (int)resp.StatusCode == 400)
        {
            return await SendTranscriptionRequestAsync(config, audioPath, useVerboseJson: false, ct).ConfigureAwait(false);
        }

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ASR API 返回错误 {(int)resp.StatusCode}: {body}");

        return body;
    }

    private static AsrTranscriptResult ParseResponse(string json, TimeSpan clipStart, TimeSpan clipEnd, string providerLabel)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string fullText = root.TryGetProperty("text", out var textProp) ? (textProp.GetString()?.Trim() ?? "") : "";
        var segments = new List<AsrTranscriptSegment>();

        if (root.TryGetProperty("segments", out var segArr) && segArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segArr.EnumerateArray())
            {
                double s = item.TryGetProperty("start", out var sp) ? sp.GetDouble() : 0;
                double e = item.TryGetProperty("end", out var ep) ? ep.GetDouble() : s;
                string t = item.TryGetProperty("text", out var tp) ? (tp.GetString()?.Trim() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(t))
                {
                    segments.Add(new AsrTranscriptSegment(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t));
                }
            }
        }

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(fullText))
        {
            segments.Add(new AsrTranscriptSegment(TimeSpan.Zero, clipEnd - clipStart, fullText));
        }

        return new AsrTranscriptResult(clipStart, clipEnd, fullText, segments, providerLabel, DateTimeOffset.UtcNow);
    }

    private string BuildCachePath(string videoPath, TimeSpan start, TimeSpan end, string model)
    {
        var fi = new FileInfo(videoPath);
        string rawKey = $"{Path.GetFullPath(videoPath)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{(long)start.TotalMilliseconds}|{(long)end.TotalMilliseconds}|{model}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"{hash}.asr.json");
    }
}
```

### 6.2 Ed25519 验签与 `AssemblyLoadContext` 插件加载器 (`LiveMonitor.Platforms/PluginLoader.cs`)

核心要点：使用 `BouncyCastle.Cryptography` 包（namespace `Org.BouncyCastle.*`，纯托管 C#，实测 2.7.0）做 Ed25519 公钥验签、内存流加载防文件锁死、共享 `LiveMonitor.Core` 与 `LiveMonitor.Platforms.Common` 类型边界避免 `InvalidCastException`。

```csharp
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using LiveMonitor.Core.Interfaces;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace LiveMonitor.Platforms;

public static class Ed25519Verifier
{
    // 开发者离线生成的 Ed25519 公钥（32 字节 Base64），私钥仅保存在开发者本地用于 CI/发版签名
    public const string TrustedPublicKeyBase64 = "REPLACE_WITH_YOUR_32_BYTE_ED25519_PUBLIC_KEY_BASE64";

    public static bool Verify(byte[] payloadBytes, string signatureBase64, string? expectedSha256Hex = null, string? publicKeyBase64 = null)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256Hex))
        {
            string actualSha256 = Convert.ToHexString(SHA256.HashData(payloadBytes));
            if (!actualSha256.Equals(expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            byte[] pubKeyBytes = Convert.FromBase64String(publicKeyBase64 ?? TrustedPublicKeyBase64);
            byte[] sigBytes = Convert.FromBase64String(signatureBase64.Trim());

            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(pubKeyBytes, 0));
            verifier.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
            return verifier.VerifySignature(sigBytes);
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class PlatformPluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "LiveMonitor.Core",
        "LiveMonitor.Platforms.Common"
    };

    private readonly AssemblyDependencyResolver? _resolver;

    public PlatformPluginLoadContext(string name, string? pluginMainDllPath)
        : base(name, isCollectible: true)
    {
        if (!string.IsNullOrEmpty(pluginMainDllPath) && File.Exists(pluginMainDllPath))
        {
            _resolver = new AssemblyDependencyResolver(pluginMainDllPath);
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 核心类型与公共契约必须回退至 Default 上下文，确保与主程序共享同一类型身份
        if (assemblyName.Name is null || SharedAssemblies.Contains(assemblyName.Name))
        {
            return null;
        }

        string? depPath = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (depPath is not null && File.Exists(depPath))
        {
            // 使用内存流加载，避免锁死磁盘 DLL 影响后续热更新覆盖
            using var ms = new MemoryStream(File.ReadAllBytes(depPath));
            return LoadFromStream(ms);
        }

        return null;
    }
}

public sealed class LoadedPluginHandle : IDisposable
{
    public IPlatformPlugin Plugin { get; }
    private readonly PlatformPluginLoadContext? _alc;

    internal LoadedPluginHandle(IPlatformPlugin plugin, PlatformPluginLoadContext? alc)
    {
        Plugin = plugin;
        _alc = alc;
    }

    public void Dispose()
    {
        _alc?.Unload();
    }
}

public sealed class PlatformPluginLoader
{
    /// <summary>
    /// 验签并从磁盘加载单个外部插件 DLL；校验失败直接抛出 SecurityException
    /// </summary>
    public LoadedPluginHandle LoadVerifiedPlugin(string dllPath, string signatureBase64, string? expectedSha256 = null)
    {
        byte[] dllBytes = File.ReadAllBytes(dllPath);
        if (!Ed25519Verifier.Verify(dllBytes, signatureBase64, expectedSha256))
        {
            throw new System.Security.SecurityException($"插件签名校验失败，已拒绝加载: {Path.GetFileName(dllPath)}");
        }

        var alc = new PlatformPluginLoadContext($"Plugin_{Path.GetFileNameWithoutExtension(dllPath)}_{Guid.NewGuid():N}", dllPath);
        try
        {
            using var ms = new MemoryStream(dllBytes);
            Assembly asm = alc.LoadFromStream(ms);

            var pluginType = asm.GetTypes().FirstOrDefault(t =>
                !t.IsAbstract && !t.IsInterface && typeof(IPlatformPlugin).IsAssignableFrom(t));

            if (pluginType is null)
                throw new InvalidOperationException($"插件中未找到 IPlatformPlugin 实现: {Path.GetFileName(dllPath)}");

            var instance = (IPlatformPlugin)(Activator.CreateInstance(pluginType)
                ?? throw new InvalidOperationException($"无法实例化插件类型: {pluginType.FullName}"));

            return new LoadedPluginHandle(instance, alc);
        }
        catch
        {
            alc.Unload();
            throw;
        }
    }
}
```

---

## 7. V0.5 三阶段开发执行清单

### 阶段 A：剪辑台云端 ASR（约 1.5 天）
- [ ] **Step 1：落地 Core ASR 契约与 `OpenAiCompatibleAsrProvider`**
  * 新增 `AsrTranscriptSegment`、`AsrTranscriptResult`、`IAsrProvider` 与 `AppSettingKeys`；在 `LiveMonitor.Media/Asr` 实现 FFmpeg 抽音、多预设请求、双格式解析与本地缓存，补齐单测。
- [ ] **Step 2：接入设置页配置与 `ClipStudioView` 交互**
  * 在 `SettingsWindow` 新增「语音识别（ASR）」分区（4 套预设 + DPAPI 加密 Key + 连通性测试；沿用现有分区式 ScrollViewer）；在剪辑台左侧高能卡片和右侧选区接入 `[🎙 转写]` 按钮与点击单句跳转。

### 阶段 B：热更新底座 + YouTube（约 3 天）
- [ ] **Step 3：落地 `IPlatformPlugin`、`PlatformPluginLoader` 与 `CliUpdater`**
  * 编写 Ed25519 签名工具脚本（供你发版时一键给 DLL 签名生成 `providers-manifest.json`）；落地带验签的 `PlatformPluginLoader`、`PluginUpdateService` 与 `yt-dlp.exe` 自动更新器；将现有 B站、Twitch 适配为 `IPlatformPlugin` 入口并补齐验签/篡改拦截单测。
- [ ] **Step 4：新增 `LiveMonitor.Platforms.YouTube` 插件**
  * 复用 `YtDlpRunner` 实现拉流解析；实现 `YouTubeDanmakuClient`（InnerTube `get_live_chat` 轮询与 `timeoutMs` 退避）；在 `WebView2` 登录页增加 YouTube 登录支持。
- [ ] **Step 5：接入「组件与插件更新」设置 UI 并验收阶段 B**
  * 展示各平台插件当前版本、远程最新版本、更新状态及 `yt-dlp` 版本；验证 YouTube 监控 + 弹幕 + 全程录制 + 剪辑导出全链路。

### 阶段 C：国内三大平台插件补齐（按平台逐个交付，约 5~7 天）
- [ ] **Step 6：新增 `LiveMonitor.Platforms.Huya`（虎牙）**
  * 实现虎牙流地址鉴权解析、轻量 Tars/Wup 编解码与 `HuyaDanmakuClient`，扩展 `WebView2` 登录支持并补齐协议单测。
- [ ] **Step 7：新增 `LiveMonitor.Platforms.Douyu`（斗鱼）**
  * 引入 `Jint` 实现斗鱼签名执行（支持 `extraScripts` 热更新 `.js`）与 STT 二进制弹幕客户端 `DouyuDanmakuClient`，补齐单测。
- [ ] **Step 8：新增 `LiveMonitor.Platforms.Douyin`（抖音）**
  * 扩展 `WebView2` 捕获 `ttwid` 与会话 Cookie；实现抖音流解析、`Jint` 签名与 Protobuf 弹幕解包客户端 `DouyinDanmakuClient`，补齐风控降级提示与单测。
- [ ] **Step 9：全平台联调与 `v0.5.0` 公开打包准备**
  * 清理/归档早期 `spikes/` 目录，检查 6 大平台注册、监控、录制、剪辑与热更新全链路，生成 `v0.5.0` 发行说明。