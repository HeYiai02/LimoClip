using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms.CustomStream;

/// <summary>
/// 用于无弹幕平台（<see cref="PlatformType.CustomStream"/>）的空实现，避免调用方判空崩溃。
/// 清流没有弹幕服务器，连接即成功但永远不产生消息。
/// </summary>
public sealed class NullDanmakuClient : IDanmakuClient
{
    public NullDanmakuClient(PlatformType platform, string roomId)
    {
        Platform = platform;
        RoomId = roomId;
    }

    public PlatformType Platform { get; }

    public string RoomId { get; }

    public event Action<DanmakuMessage>? MessageReceived
    {
        add { }
        remove { }
    }

    public event Action<Exception?>? Disconnected
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
