# LimoClip V0.2（弹幕引擎与高能空跑）— 详细设计实施文档

> **前置条件**：启动 V0.2 编码前，建议先按 `STEP8_REPORT.md` 完成 V0.1 的 **20~30 路极限性能摸底**，拿到纯视频解码（`d3d11va-copy` + `aid=auto` + `15fps`）的 CPU / 内存 / GPU 基线数据，便于在 V0.2 叠加多路弹幕后准确归因性能开销。

---

## 1. V0.2 范围边界与验收目标

### 1.1 明确边界（In-Scope / Out-of-Scope）

* **本期包含（In-Scope）**：
  * 新增 `LiveMonitor.Danmaku` 与 `LiveMonitor.Danmaku.Tests` 两个项目，复用 V0.1 已建好的 `DanmakuBlockKeywords`、`AppSettings`、`PlatformCredentials` 表与 `LayoutSlots.DefaultDanmakuVisible` 字段。
  * 取消 `IPlatformProvider.CreateDanmakuClient` 注释，在 `Platforms.Bilibili` 和 `Platforms.Twitch` 落地实时弹幕客户端（WebSocket + 心跳 + 断线重连）。
  * 实现 `DanmakuManager`：按 `(Platform, RoomId)` 单例复用连接与引用计数，主分屏、焦点全屏浮层共享同一条弹幕流。
  * 落地本地过滤管线：跨平台 `KeywordFilter`（普通包含 + 正则）与 **仅 B站启用、仅检查单用户** 的 `BilibiliUnicycleFilter`（独轮车过滤器，豁免 $\le 6$ 字短情绪词）。
  * 打通单路分屏底部控制条的「弹幕开/关」按钮与设置页开关，在 `MpvPlayerView` 上叠加轻量级 `DanmakuOverlay`（默认关闭，按需开启）。
  * 落地 `SlidingWindowDetector` 高能检测器（**仅空跑打点，暂不录屏**），将触发的高能事件输出至底部状态栏与独立「高能事件流」抽屉面板，便于用真实直播调优阈值。
* **本期明确不做（Out-of-Scope，顺延至 V0.3 / V0.4）**：
  * FFmpeg 环形缓冲/全程录制、伴随弹幕文件落盘（`.jsonl` / `.xml`）、高能自动切片合并与云盘/NAS 归档（V0.3）。
  * 弹幕密度波形剪辑台、ASS 弹幕烧录导出、ASR 语音转写、抖音/斗鱼/虎牙/YouTube 接入与 DLL 热更新（V0.4）。

### 1.2 验收标准

1. **连接复用与稳定性**：
   * 同一直播间在主分屏与双击进入的焦点全屏浮层同时开启弹幕时，抓包/日志确认底层仅维持 **1 条** WebSocket 连接；关闭浮层不影响主分屏弹幕，关闭分屏后连接彻底释放。
   * 断网后恢复，活跃弹幕客户端能在指数退避（3s → 10s → 30s）内自动重连。
2. **过滤准确率**：
   * 单元测试覆盖 `BilibiliUnicycleFilter` 三条规则与 $\le 6$ 字短情绪词豁免；Twitch 弹幕确认仅过 `KeywordFilter`、不进入独轮车判定。
   * 真实 B站高热度直播间挂载测试，独轮车拦截计数实时更新且无明显误杀正常互动。
3. **渲染与高能空跑**：
   * 6~9 路高热度直播间同时开启画面弹幕预览，UI 操作流畅无明显卡顿（记录相对 V0.1 基线的 CPU/内存增量）；若出现掉帧，记录数据以决定是否切入 `libmpv` 内置 `libass` 通道。
   * 高能检测器在冷清直播间（每分钟 $< 10$ 条弹幕）不产生误报；在真实团战/名场面爆发时能在 10 秒内准确打出 `HighlightEvent` 日志。

---

## 2. V0.2 编码前设计评审修正项（防坑指南）

| 序号 | 潜在隐患 | 强制修正方案 |
| :--- | :--- | :--- |
| **1** | **B站游客态 UID 脱敏导致独轮车误杀全员**：B站未携带有效登录 Cookie（`SESSDATA` + `buvid3`）连入弹幕服务器时，下发的 `DANMU_MSG` 中发送者 `uid` 全为 `0` 且用户名被脱敏。若把 `uid="0"` 当作同一个用户，所有人发弹幕都会触发单用户复读规则，导致整间房被误屏蔽。 | ① `BilibiliDanmakuClient` 优先复用 `ICredentialStore` 中已绑定的 B站 Cookie 认证；② `BilibiliUnicycleFilter` 强制校验 `SenderUid`：若为 `"0"` 或空字符串，直接放行跳过独轮车统计，并在分屏/日志提示“未登录 B站，独轮车单用户识别已降级”。 |
| **2** | **焦点全屏浮层重复建立弹幕连接**：V0.1 焦点浮层是独立 `MpvPlayerView`，若由播放器自行创建 `IDanmakuClient`，双击放大会开第二条 WS 连接并导致高能计数翻倍。 | 播放器与浮层均不直接持有 `IDanmakuClient`，统一向 `DanmakuManager.Subscribe(platform, roomId)` 订阅；管理器按 `(Platform, RoomId)` 维护引用计数，归零时才断开 WS。 |
| **3** | **多路弹幕海拖垮 Avalonia UI 线程**：热门直播间每秒上百条弹幕，直接 `Dispatcher.UIThread.Post` 逐条创建 `TextBlock` 会导致 UI 假死和 GC 飙升。 | ① 采用 50ms 批量合帧入队（`Channel<DanmakuMessage>` 消费）；② `DanmakuOverlay` 采用固定轨道池 + **单窗同屏上限（默认 30 条，小窗 $< 360\text{px}$ 上限 15 条）**，超限直接丢弃画面渲染（但**绝不丢弃**送往 `SlidingWindowDetector` 的统计流）。 |
| **4** | **冷清直播间高能检测误报**：平时每 10 秒只有 1 条弹幕的房间，突然来了 4 条弹幕就满足 $4\times$ 密度倍数，导致频繁假警报。 | `SlidingWindowDetector` 必须同时满足三个条件才触发：**倍数阈值**（如 $\ge 3.0\times$ 基线）+ **绝对速率下限**（如 10 秒内至少 15 条有效弹幕）+ **冷却期**（同房间 60 秒内不重复触发）。 |
| **5** | **长时间挂机过滤器内存泄漏**：24 小时监控热门大房间会产生数十万个独立 `SenderUid`，字典只增不减会撑爆内存。 | `BilibiliUnicycleFilter` 内置惰性清理机制：每处理 2000 条弹幕或间隔 5 分钟，自动驱逐超过 15 分钟无发言且已过静默期的用户状态记录。 |

---

## 3. 新增项目结构与 Core 层接口契约

### 3.1 新增目录结构

```text
src/
├── LiveMonitor.Core/
│   ├── Models/
│   │   ├── DanmakuMessage.cs            # 统一弹幕消息模型（含 Platform）
│   │   ├── HighlightEvent.cs            # 高能打点事件模型
│   │   └── HighlightRuleOptions.cs      # 高能检测参数配置
│   └── Interfaces/
│       ├── IDanmakuClient.cs
│       ├── IDanmakuFilter.cs
│       └── IHighlightDetector.cs
│
├── LiveMonitor.Platforms.Bilibili/
│   ├── BilibiliPacketParser.cs          # 16字节大端协议头 + Zlib/Brotli 解压 + DANMU_MSG 解析
│   └── BilibiliDanmakuClient.cs         # getDanmuInfo + WebSocket + 30s 心跳 + 重连
│
├── LiveMonitor.Platforms.Twitch/
│   └── TwitchDanmakuClient.cs           # IRC WebSocket (wss://irc-ws.chat.twitch.tv:443)
│
└── LiveMonitor.Danmaku/                 # [新建类库，仅依赖 Core]
    ├── DanmakuManager.cs                # 单例复用、过滤管线串联、高能分发
    ├── Filters/
    │   ├── KeywordFilter.cs             # 跨平台关键词/正则过滤
    │   └── BilibiliUnicycleFilter.cs    # 仅 B站单用户独轮车过滤器
    └── Analyzers/
        └── SlidingWindowDetector.cs     # 1秒分桶滑动窗口高能检测器
```

### 3.2 Core 层核心契约定义

```csharp
namespace LiveMonitor.Core.Models;

public record DanmakuMessage(
    PlatformType Platform,
    string RoomId,
    string SenderUid,
    string SenderName,
    string Content,
    DateTimeOffset Timestamp,
    bool IsGiftOrSuperChat = false
);

public record HighlightRuleOptions(
    int ShortWindowSeconds = 10,
    int BaselineWindowSeconds = 180,
    double DensityMultiplier = 3.0,
    int MinShortWindowCount = 15,
    int CooldownSeconds = 60,
    IReadOnlyList<string>? BoostKeywords = null
);

public record HighlightEvent(
    PlatformType Platform,
    string RoomId,
    DateTimeOffset TriggerTime,
    double CurrentRatePerSec,
    double BaselineRatePerSec,
    string TriggerReason
);
```

```csharp
namespace LiveMonitor.Core.Interfaces;

public interface IDanmakuClient : IAsyncDisposable
{
    event Action<DanmakuMessage>? OnMessageReceived;
    event Action<Exception?>? OnDisconnected;
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
}

public interface IDanmakuFilter
{
    /// <summary>返回 true 表示放行，false 表示被拦截</summary>
    bool ShouldAllow(DanmakuMessage message);
}

public interface IHighlightDetector
{
    HighlightEvent? Ingest(DanmakuMessage message);
    void UpdateRules(HighlightRuleOptions options);
}
```

---

## 4. 核心模块设计规范

### 4.1 `DanmakuManager`（单例复用与管线编排）
* 内部维护 `ConcurrentDictionary<(PlatformType, string), RoomDanmakuSession> _sessions`。
* 每个 `RoomDanmakuSession` 持有：引用计数 `RefCount`、1 个 `IDanmakuClient`、1 个 `SlidingWindowDetector`、以及 UI 订阅回调集合。
* **数据流向（严格顺序）**：
  `IDanmakuClient` 原始弹幕 $\rightarrow$ `KeywordFilter` $\rightarrow$ （若为 B站且设置开启）`BilibiliUnicycleFilter` $\rightarrow$ ① 推送至 `SlidingWindowDetector` 统计高能 $\rightarrow$ ② 仅当该房间有开启画面弹幕的订阅者时，推送至对应 `DanmakuOverlay` 渲染队列。
* **断线重连**：监听 `OnDisconnected`，在 `RefCount > 0` 时按 3s → 10s → 30s 退避重连（B站重连时需重新调 `getDanmuInfo` 刷新 `token`）。

### 4.2 `TwitchDanmakuClient`（IRC WebSocket 协议要点）
* 连接地址：`wss://irc-ws.chat.twitch.tv:443`。
* 握手序列：
  1. `CAP REQ :twitch.tv/tags twitch.tv/commands`
  2. 匿名态：`PASS SCHMOOPIIE` + `NICK justinfan{Random(10000,99999)}`（绑定了 `auth-token` 时可传 `PASS oauth:{token}`）。
  3. `JOIN #{roomId.ToLowerInvariant()}`。
* 保活：收到服务端 `PING :tmi.twitch.tv` 时立即回复 `PONG :tmi.twitch.tv`。
* 消息解析：解析 `PRIVMSG #{channel} :{content}`，从前置 `@` 标签提取 `user-id` 与 `display-name`，构造 `DanmakuMessage`。

### 4.3 `DanmakuOverlay`（轻量级叠加层）
* 叠放在 `MpvPlayerView` 的视频层上方、HUD 控制条下方，设置 `IsHitTestVisible="False"` 确保不阻断鼠标双击焦点和拖拽操作。
* 采用轻量级 `Canvas` + `DispatcherTimer`（30fps 驱动位置更新，而非为每条弹幕创建独立 Avalonia 动画）或 `DrawingContext` 自绘，控制同屏上限（默认 30 条），超出上限直接丢弃新入队渲染项。

### 4.4 `SlidingWindowDetector`（高能空跑与事件面板）
* 为每个房间维护长度为 180 的环形秒级计数数组（对应 3 分钟历史）与当前秒游标。
* 每收到一条通过过滤的弹幕，当前秒桶加 1（若命中加权词如 `666`、`草`、`神回`、`Pog`，额外 $+0.5$ 权重）。
* 计算近 10 秒平均速率 $R_{\text{short}}$ 与此前 170 秒基线速率 $R_{\text{base}} = \max(\text{avg}, 0.5)$。
* 当 $R_{\text{short}} \ge R_{\text{base}} \times \text{Multiplier}$ 且 10 秒总数 $\ge \text{MinShortWindowCount}$ 且距上次触发 $\ge 60\text{s}$ 时，发射 `HighlightEvent`，在主窗口右侧/底部折叠抽屉中追加一条可观测日志（时间、房间 `#编号 主播名`、峰值速率、触发原因）。

---

## 5. 核心 C# 代码实现参考

### 5.1 B站 WebSocket 协议解包器 (`LiveMonitor.Platforms.Bilibili/BilibiliPacketParser.cs`)

零外部压缩依赖，完全基于 .NET 原生 `BinaryPrimitives`、`System.IO.Compression.ZLibStream` 与 `BrotliStream`，内置递归深度保护防畸形包攻击。

```csharp
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms.Bilibili;

public static class BilibiliPacketParser
{
    public const int HeaderSize = 16;

    // ProtocolVersion 常量
    public const short ProtoJson = 0;
    public const short ProtoHeartbeat = 1;
    public const short ProtoZlib = 2;
    public const short ProtoBrotli = 3;

    // Operation 常量
    public const int OpClientHeartbeat = 2;
    public const int OpHeartbeatReply = 3;
    public const int OpNotification = 5;
    public const int OpClientAuth = 7;
    public const int OpAuthReply = 8;

    private const int MaxDecompressDepth = 3;

    /// <summary>
    /// 封包：构造 16 字节大端头 + 负载（用于发送认证包 Op=7 或心跳包 Op=2）
    /// </summary>
    public static byte[] BuildPacket(int operation, string bodyText, short protoVersion = ProtoHeartbeat, int sequenceId = 1)
    {
        var bodyBytes = string.IsNullOrEmpty(bodyText) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(bodyText);
        var totalLength = HeaderSize + bodyBytes.Length;
        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteInt32BigEndian(span[0..4], totalLength);
        BinaryPrimitives.WriteInt16BigEndian(span[4..6], HeaderSize);
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], protoVersion);
        BinaryPrimitives.WriteInt32BigEndian(span[8..12], operation);
        BinaryPrimitives.WriteInt32BigEndian(span[12..16], sequenceId);

        if (bodyBytes.Length > 0)
        {
            bodyBytes.CopyTo(span[HeaderSize..]);
        }

        return buffer;
    }

    /// <summary>
    /// 构造进房认证包 JSON 字节流
    /// </summary>
    public static byte[] BuildAuthPacket(long realRoomId, string token, long uid = 0, string? buvid3 = null)
    {
        var payload = new
        {
            uid,
            roomid = realRoomId,
            protover = 3,
            buvid = buvid3 ?? string.Empty,
            platform = "web",
            type = 2,
            key = token
        };
        var json = JsonSerializer.Serialize(payload);
        return BuildPacket(OpClientAuth, json, ProtoHeartbeat);
    }

    /// <summary>
    /// 构造 30 秒心跳包
    /// </summary>
    public static byte[] BuildHeartbeatPacket()
        => BuildPacket(OpClientHeartbeat, "[object Object]", ProtoHeartbeat);

    /// <summary>
    /// 解包：解析单个或多个粘连/压缩的 B站二进制帧，提取所有 DANMU_MSG
    /// </summary>
    public static void ParseFrame(
        ReadOnlySpan<byte> frame,
        string roomId,
        List<DanmakuMessage> output,
        int depth = 0)
    {
        if (depth > MaxDecompressDepth) return;

        int offset = 0;
        while (offset + HeaderSize <= frame.Length)
        {
            int packetLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(offset, 4));
            short headerLength = BinaryPrimitives.ReadInt16BigEndian(frame.Slice(offset + 4, 2));
            short protoVersion = BinaryPrimitives.ReadInt16BigEndian(frame.Slice(offset + 6, 2));
            int operation = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(offset + 8, 4));

            if (packetLength < HeaderSize || headerLength < HeaderSize || offset + packetLength > frame.Length)
            {
                break; // 非法或截断帧，安全退出
            }

            var body = frame.Slice(offset + headerLength, packetLength - headerLength);

            if (operation == OpNotification)
            {
                switch (protoVersion)
                {
                    case ProtoJson:
                    case ProtoHeartbeat:
                        TryParseDanmuJson(body, roomId, output);
                        break;

                    case ProtoZlib:
                        var zlibDecompressed = DecompressZlib(body);
                        ParseFrame(zlibDecompressed, roomId, output, depth + 1);
                        break;

                    case ProtoBrotli:
                        var brotliDecompressed = DecompressBrotli(body);
                        ParseFrame(brotliDecompressed, roomId, output, depth + 1);
                        break;
                }
            }

            offset += packetLength;
        }
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// 解析单条 JSON 通知，提取 DANMU_MSG（兼容 DANMU_MSG:4:0:2:2:2:0 等变体）
    /// </summary>
    private static void TryParseDanmuJson(ReadOnlySpan<byte> utf8Json, string roomId, List<DanmakuMessage> output)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            var root = doc.RootElement;

            if (!root.TryGetProperty("cmd", out var cmdProp)) return;
            var cmd = cmdProp.GetString();
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal))
            {
                return;
            }

            if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 3)
            {
                return;
            }

            // info[1]: 弹幕文本内容
            var content = info[1].GetString();
            if (string.IsNullOrWhiteSpace(content)) return;

            // info[2]: [uid, uname, ...]
            var userArr = info[2];
            string senderUid = "0";
            string senderName = string.Empty;

            if (userArr.ValueKind == JsonValueKind.Array && userArr.GetArrayLength() >= 2)
            {
                senderUid = userArr[0].ValueKind == JsonValueKind.Number
                    ? userArr[0].GetInt64().ToString()
                    : (userArr[0].GetString() ?? "0");
                senderName = userArr[1].GetString() ?? string.Empty;
            }

            // info[0][4]: 毫秒时间戳（可选，缺省取当前 UTC 时间）
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            if (info[0].ValueKind == JsonValueKind.Array && info[0].GetArrayLength() > 4 &&
                info[0][4].ValueKind == JsonValueKind.Number &&
                info[0][4].TryGetInt64(out var unixMs) && unixMs > 1_000_000_000_000)
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }

            output.Add(new DanmakuMessage(
                Platform: PlatformType.Bilibili,
                RoomId: roomId,
                SenderUid: senderUid,
                SenderName: senderName,
                Content: content,
                Timestamp: timestamp,
                IsGiftOrSuperChat: false
            ));
        }
        catch (JsonException)
        {
            // 忽略偶发非标准 JSON 通知，防止单条脏包中断整个接收循环
        }
    }
}
```

---

### 5.2 B站单用户独轮车过滤器 (`LiveMonitor.Danmaku/Filters/BilibiliUnicycleFilter.cs`)

严格落实最新边界：**仅对 `PlatformType.Bilibili` 生效、仅检查单用户（不跨用户）、豁免 $\le 6$ 字短情绪词、跳过游客态 `uid="0"`、内置线程安全与过期状态清理**。

```csharp
using System.Collections.Concurrent;
using System.Text;
using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Danmaku.Filters;

public sealed class BilibiliUnicycleFilter : IDanmakuFilter
{
    private readonly record struct UserMsgRecord(string NormalizedText, int RawLength, DateTimeOffset Timestamp);

    private sealed class UserState
    {
        public readonly object SyncRoot = new();
        public readonly Queue<UserMsgRecord> RecentMessages = new();
        public DateTimeOffset MutedUntil { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastActive { get; set; } = DateTimeOffset.MinValue;
    }

    // Key = "{RoomId}:{SenderUid}"，避免同一用户跨房间状态互相污染
    private readonly ConcurrentDictionary<string, UserState> _users = new(StringComparer.Ordinal);
    private long _processedCount;
    private long _blockedCount;

    // 规则阈值常量
    private static readonly TimeSpan Window60s = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window30s = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Window15s = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MuteDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EvictIdleThreshold = TimeSpan.FromMinutes(15);

    private const int ShortEmotionMaxLength = 6;     // <= 6 字短情绪词豁免复读拦截
    private const double SimilarityThreshold = 0.80; // 80% 清洗后相似度阈值
    private const double IntervalStdDevMaxSec = 0.5; // 连续 4 条机械间隔标准差上限

    public bool IsEnabled { get; set; } = true;
    public long BlockedCount => Interlocked.Read(ref _blockedCount);

    public bool ShouldAllow(DanmakuMessage message)
    {
        if (!IsEnabled) return true;

        // 1. 仅针对 B站生效；Twitch 与其他平台直接放行
        if (message.Platform != PlatformType.Bilibili) return true;

        // 2. 礼物/醒目留言不走独轮车拦截
        if (message.IsGiftOrSuperChat) return true;

        // 3. 防误伤：未登录游客态下发 uid="0" 时跳过单用户追踪，防止把全房观众误判为同一人
        if (string.IsNullOrWhiteSpace(message.SenderUid) || message.SenderUid == "0")
        {
            return true;
        }

        // 惰性内存清理（每 2000 条执行一次后台驱逐）
        if (Interlocked.Increment(ref _processedCount) % 2000 == 0)
        {
            EvictStaleUsers(message.Timestamp);
        }

        var key = $"{message.RoomId}:{message.SenderUid}";
        var state = _users.GetOrAdd(key, _ => new UserState());

        lock (state.SyncRoot)
        {
            var now = message.Timestamp;
            state.LastActive = now;

            // 若当前处于 10 分钟静默黑名单期，直接拦截
            if (now < state.MutedUntil)
            {
                Interlocked.Increment(ref _blockedCount);
                return false;
            }

            var normalized = NormalizeText(message.Content);
            if (normalized.Length == 0) return true;

            // 清理超过 60 秒的旧记录
            while (state.RecentMessages.Count > 0 && (now - state.RecentMessages.Peek().Timestamp) > Window60s)
            {
                state.RecentMessages.Dequeue();
            }

            state.RecentMessages.Enqueue(new UserMsgRecord(normalized, message.Content.Trim().Length, now));

            // 规则 2：长文本连发（30 秒内连续 >= 3 条长度 > 15 字，小说/文案切片特征）
            if (IsLongTextBurst(state.RecentMessages, now))
            {
                MuteAndCount(state, now);
                return false;
            }

            // 硬性豁免：长度 <= 6 字的短情绪词（666、？？？、哈哈哈、神回等）不参与复读拦截
            if (normalized.Length <= ShortEmotionMaxLength)
            {
                return true;
            }

            // 规则 3：同账号 15 秒内发送 > 3 条完全一致的长句（长度 > 10 字）
            if (normalized.Length > 10 && IsIdenticalLongRepeat15s(state.RecentMessages, normalized, now))
            {
                MuteAndCount(state, now);
                return false;
            }

            // 规则 1：60 秒内发送 > 3 条相似度 > 80% 的弹幕，或连续 4 条发言间隔标准差 < 0.5 秒
            if (IsMechanicalRepeat60s(state.RecentMessages, normalized))
            {
                MuteAndCount(state, now);
                return false;
            }

            return true;
        }
    }

    private void MuteAndCount(UserState state, DateTimeOffset now)
    {
        state.MutedUntil = now + MuteDuration;
        Interlocked.Increment(ref _blockedCount);
    }

    private static bool IsLongTextBurst(Queue<UserMsgRecord> records, DateTimeOffset now)
    {
        int longCount = 0;
        foreach (var r in records)
        {
            if ((now - r.Timestamp) <= Window30s && r.RawLength > 15)
            {
                longCount++;
                if (longCount >= 3) return true;
            }
        }
        return false;
    }

    private static bool IsIdenticalLongRepeat15s(Queue<UserMsgRecord> records, string currentNormalized, DateTimeOffset now)
    {
        int sameCount = 0;
        foreach (var r in records)
        {
            if ((now - r.Timestamp) <= Window15s &&
                r.NormalizedText.Length > 10 &&
                string.Equals(r.NormalizedText, currentNormalized, StringComparison.Ordinal))
            {
                sameCount++;
                if (sameCount > 3) return true;
            }
        }
        return false;
    }

    private static bool IsMechanicalRepeat60s(Queue<UserMsgRecord> records, string currentNormalized)
    {
        // 条件 1A：60 秒内超过 3 条清洗后相似度 > 80% 的非短词弹幕
        int similarCount = 0;
        foreach (var r in records)
        {
            if (r.NormalizedText.Length <= ShortEmotionMaxLength) continue;
            if (ComputeSimilarity(r.NormalizedText, currentNormalized) > SimilarityThreshold)
            {
                similarCount++;
                if (similarCount > 3) return true;
            }
        }

        // 条件 1B：最近连续 4 条非短词发言的时间间隔标准差 < 0.5 秒（脚本定时器特征）
        var recentList = records.Where(r => r.NormalizedText.Length > ShortEmotionMaxLength).TakeLast(4).ToList();
        if (recentList.Count == 4)
        {
            double i1 = (recentList[1].Timestamp - recentList[0].Timestamp).TotalSeconds;
            double i2 = (recentList[2].Timestamp - recentList[1].Timestamp).TotalSeconds;
            double i3 = (recentList[3].Timestamp - recentList[2].Timestamp).TotalSeconds;

            // 排除极短瞬发误判（如网络粘包导致 3 个间隔全为 0）
            double mean = (i1 + i2 + i3) / 3.0;
            if (mean >= 0.8)
            {
                double variance = ((i1 - mean) * (i1 - mean) + (i2 - mean) * (i2 - mean) + (i3 - mean) * (i3 - mean)) / 3.0;
                double stdDev = Math.Sqrt(variance);
                if (stdDev < IntervalStdDevMaxSec)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 文本清洗：去除空白、标点、符号及常见防屏蔽干扰符，保留中英文与数字
    /// </summary>
    public static string NormalizeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 基于 Levenshtein 编辑距离的归一化相似度 [0, 1]（弹幕短文本场景下分配极小、速度快）
    /// </summary>
    public static double ComputeSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 1.0;

        // 长度差异过大时剪枝，避免无效计算
        if ((double)Math.Min(a.Length, b.Length) / maxLen <= SimilarityThreshold)
        {
            return 0.0;
        }

        int n = a.Length;
        int m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return 1.0 - (double)prev[m] / maxLen;
    }

    private void EvictStaleUsers(DateTimeOffset now)
    {
        foreach (var kv in _users)
        {
            if ((now - kv.Value.LastActive) > EvictIdleThreshold && now >= kv.Value.MutedUntil)
            {
                _users.TryRemove(kv.Key, out _);
            }
        }
    }
}
```

---

## 6. V0.2 6 步开发执行清单

- [x] **Step 1：补齐 Core 弹幕契约与创建 `LiveMonitor.Danmaku` 项目（0.5 天）**
  * 在 `Core` 中新增 `DanmakuMessage`、`HighlightEvent`、`HighlightRuleOptions` 与 `IDanmakuClient` / `IDanmakuFilter` / `IHighlightDetector`；新建 `LiveMonitor.Danmaku` 与 `LiveMonitor.Danmaku.Tests` 并加入 `LiveMonitor.sln`。
  * *实施偏差*：未按原计划取消 `IPlatformProvider.CreateDanmakuClient` 注释，而是新增 Core `IDanmakuClientFactory`，由 `LiveMonitor.Platforms.DanmakuClientFactory` 实现（`Danmaku` 层只依赖 Core）。
- [x] **Step 2：落地 B站 协议解包器与 `BilibiliDanmakuClient`（1.5 天）**
  * 落地 `BilibiliPacketParser`（含 Zlib / Brotli 解压单测）；在 `Platforms.Bilibili` 实现 `getDanmuInfo`、`ClientWebSocket` 认证与 30 秒心跳；透传已绑定的 B站 Cookie 确保拿到真实 `uid`。
  * *实施偏差*：WebView2 登录 Cookie 白名单没有 `buvid3`，缺失时用无名 `spi` 接口兜底；V0.2 只收不发，游客态可连接（uid=0 时独轮车按设计放行）。B站已对 `getDanmuInfo` 加 WBI 签名风控（未签名返回 `-352`），补 `BilibiliWbiSigner`（mixin 置换 + `w_rid`，密钥来自匿名 `nav` 接口并缓存 1 小时）。36 个测试。
- [x] **Step 3：落地 `TwitchDanmakuClient`（0.5 天）**
  * 在 `Platforms.Twitch` 实现 IRC WebSocket 连接、匿名/OAuth 登录、`PING/PONG` 保活与 `PRIVMSG` 标签解析。
  * *实施偏差*：`auth-token` 是网站会话而非 IRC OAuth，V0.2 只做匿名只读；26 个测试。
- [x] **Step 4：落地过滤管线、`SlidingWindowDetector` 与 `DanmakuManager`（1.5 天）**
  * 实现 `KeywordFilter`（联动 SQLite `DanmakuBlockKeywords` 热更新）、`BilibiliUnicycleFilter`（补齐三条规则与短词豁免单测）、`SlidingWindowDetector` 与按 `(Platform, RoomId)` 引用计数复用的 `DanmakuManager`。
  * *实际实现*：另加 `HighlightSettingsStore`（参数持久化）与 `HighlightEventRepository`（高能事件落盘，`HighlightEvents` 表）；manager 退避 3/10/30/60s、批量 50ms、`ReloadSettingsAsync`/`UpdateHighlightRulesAsync`/`SetUnicycleEnabledAsync` 热更新。Danmaku 88 测试、Data 21 测试。
- [x] **Step 5：接入 `DanmakuOverlay` 与高能事件流面板（1.5 天）**
  * 激活分屏底部控制条的「弹幕开/关」按钮与设置页开关，实现带同屏上限的 `DanmakuOverlay`（确保主分屏与焦点浮层共享单路弹幕流）；在主界面增加可折叠的「高能打点日志」面板与状态栏独轮车拦截计数。
  * *实际实现*：全局总闸只停渲染、不断连；面板放在工作区右侧叠加层（不写 Dock 结构）；设置页开放正则词条与高能参数卡片，关闭设置即热更新；启动回填最近 200 条高能事件。
- [~] **Step 6：真实直播联调与性能对比验收（1 天）**
  * 对比 V0.1 基准数据，测试 6~9 路高热度直播间全开弹幕的 UI 帧率与内存占用；挂机验证 B站独轮车拦截准确率与高能检测阈值稳定性。
  * *当前状态*：自动化测试与本地构建全部通过；**真实网络验证已完成**——B站 `getDanmuInfo` 补 WBI 签名后游客态收弹幕 24 条（房间 21452505）、Twitch 匿名 IRC 收弹幕 3 条（ludwig）、GUI 启动冒烟正常。剩余人工挂机：弹幕海性能、独轮车误杀率、高能阈值稳定性、B站登录态 uid 非 0。

---

## 7. 实施记录（2026-11-22）

- 交付状态：`dotnet build LiveMonitor.sln` 0 错误；全仓库测试 **281/281**（Data 21、Platforms 114、Danmaku 88、Desktop 58）；改动未提交。
- 真实网络验证（2026-11-22）：B站 `getDanmuInfo` 需 WBI 签名（`BilibiliWbiSigner`，未签名 `-352`）；游客态实测 B站 21452505 收到 24 条、Twitch ludwig 收到 3 条；GUI 启动冒烟通过。详见 `HANDOFF.md`「Step 6 真实网络验证」。
- 新增工程：`src/LiveMonitor.Danmaku`、`tests/LiveMonitor.Danmaku.Tests`（sln 12 个工程）。
- 新增文件（主要）：
  - Core：`Models/DanmakuMessage.cs`、`Models/HighlightEvent.cs`、`Models/HighlightRuleOptions.cs`、`Interfaces/IDanmakuClient.cs`、`Interfaces/IDanmakuClientFactory.cs`、`Interfaces/IDanmakuFilter.cs`、`Interfaces/IHighlightDetector.cs`、`Interfaces/IHighlightEventRepository.cs`、`AppSettingKeys.cs`（追加 6 个 Highlight 键）。
  - Platforms.Bilibili：`BilibiliPacketParser.cs`、`BilibiliWbiSigner.cs`、`IBilibiliWebSocketConnection.cs`、`ClientWebSocketBilibiliConnection.cs`、`BilibiliDanmakuClient.cs`。
  - Platforms.Twitch：`TwitchIrcParser.cs`、`ITwitchIrcConnection.cs`、`ClientWebSocketTwitchConnection.cs`、`TwitchDanmakuClient.cs`。
  - Platforms：`DanmakuClientFactory.cs`。
  - Danmaku：`Filters/KeywordFilter.cs`、`Filters/BilibiliUnicycleFilter.cs`、`Analyzers/SlidingWindowDetector.cs`、`HighlightSettingsStore.cs`、`DanmakuManager.cs`、`DanmakuSessionHandle.cs`。
  - Data：`Repositories/HighlightEventRepository.cs`（`LiveMonitorDatabase.Schema` 追加 `HighlightEvents` 表与索引）。
  - Desktop：`Controls/DanmakuOverlay.cs`、`ViewModels/HighlightEventItem.cs`；改动 `Player/MpvPlayerView.axaml(.cs)`、`ViewModels/MainWindowViewModel.cs`、`Views/MainWindow.axaml(.cs)`、`Views/SettingsWindow.axaml(.cs)`、`Services/AppServices.cs`、`LiveMonitor.Desktop.csproj`。
- 与本文档其他章节的契约差异，以 `HANDOFF.md` 的「与 limoclip_v0_2.md 的关键偏差」为准。
