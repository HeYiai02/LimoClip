using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 单个直播间的实时弹幕客户端。
/// <para>
/// 约定：<see cref="ConnectAsync"/> 只做一次连接尝试，失败直接抛异常；
/// 断线重连（含 3s→10s→30s 退避）由 DanmakuManager 统一负责，
/// 避免每个平台客户端各写一套重试逻辑。
/// </para>
/// </summary>
public interface IDanmakuClient : IAsyncDisposable
{
    PlatformType Platform { get; }

    /// <summary>平台侧真实房间号（B 站为 get_info 返回的 room_id，Twitch 为小写 login）。</summary>
    string RoomId { get; }

    /// <summary>收到一条弹幕。实现方可能从后台线程触发，订阅方负责切到 UI 线程。</summary>
    event Action<DanmakuMessage>? MessageReceived;

    /// <summary>连接异常断开时触发，参数为断线原因；null 表示对端正常关闭。主动 Disconnect 不触发。</summary>
    event Action<Exception?>? Disconnected;

    Task ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();
}
